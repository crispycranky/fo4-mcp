// F4Exec - F4SE plugin that runs console commands received over a named pipe
// on the game's main thread and returns the console output they produce.
//
// Pipe: \\.\pipe\fo4_console_exec_v1 (message mode)
// Request:  "ping" | "exec\n<line>[\n<line>...]"
// Response: JSON  {"ok":true,...}  /  {"ok":false,"error":"..."}

#include "f4se/PluginAPI.h"
#include "f4se_common/f4se_version.h"

#include <windows.h>
#include <atomic>
#include <cstdio>
#include <share.h>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#define F4EXEC_VERSION 1
#define PIPE_NAME "\\\\.\\pipe\\fo4_console_exec_v1"

// ---- game addresses (RVA, runtime 1.11.240) ----
// ConsoleManager::Print(const char*): same as F4SE 0.7.9 GameAPI.h
static const uintptr_t kRVA_ConsolePrint = 0x0103C250;
// Console "executeCommand" handler target (from the GFx callback registration at 0x01036130).
// It does not run the line: it parses it and queues compiled Script objects.
static const uintptr_t kRVA_ConsoleExecute = 0x01036250;
// Per-frame processor that runs the queued console scripts, called unconditionally from
// the main loop update (0x00C339D0) at this call site.
static const uintptr_t kRVA_ProcessQueue = 0x01036880;
static const uintptr_t kRVA_ProcessQueueCall = 0x00C34794;

static const unsigned char kPrintPrologue[14] = {
	0x48, 0x83, 0xEC, 0x68,                    // sub rsp, 0x68
	0x48, 0x89, 0x5C, 0x24, 0x70,              // mov [rsp+0x70], rbx
	0x48, 0x89, 0x6C, 0x24, 0x60,              // mov [rsp+0x60], rbp
};
// UI::IsMenuOpen(const BSFixedString&). The console line handler checks IsMenuOpen("Console")
// and, when the console is closed, only echoes "Command: X" instead of running the line.
static const uintptr_t kRVA_IsMenuOpen = 0x01A80320;
// static BSFixedString "Console" returned by the console menu-name getter at 0x01A86290
static const uintptr_t kRVA_ConsoleMenuName = 0x03DBD620;
static const unsigned char kIsMenuOpenPrologue[15] = {
	0x48, 0x89, 0x5C, 0x24, 0x08,              // mov [rsp+8], rbx
	0x57,                                      // push rdi
	0x48, 0x83, 0xEC, 0x40,                    // sub rsp, 0x40
	0x33, 0xC0,                                // xor eax, eax
	0x48, 0x8B, 0xD9,                          // mov rbx, rcx
};
static const unsigned char kExecutePrologue[13] = {
	0x40, 0x55, 0x53, 0x56, 0x57, 0x41, 0x56, 0x41, 0x57,   // push rbp/rbx/rsi/rdi/r14/r15
	0x48, 0x8D, 0xAC, 0x24,                                 // lea rbp, [rsp-...]
};

typedef void (*_ConsolePrint)(void* mgr, const char* str);
typedef void (*_ConsoleExecute)(const char* line);
typedef bool (*_IsMenuOpen)(void* ui, void* name);
typedef void (*_ProcessQueue)(void* a, void* b);

static _ConsolePrint   s_origPrint = nullptr;
static _IsMenuOpen     s_origIsMenuOpen = nullptr;
static void*           s_consoleMenuName = nullptr;
static _ConsoleExecute s_execute = nullptr;
static _ProcessQueue   s_processQueue = nullptr;

static FILE* s_log = nullptr;

// capture state: only Print calls on the thread running our command are recorded
static std::atomic<DWORD> s_captureThread{ 0 };
static std::string s_captureBuf;
static bool s_captureEnabled = true;   // main thread only

static void Log(const char* fmt, ...)
{
	if (!s_log) return;
	va_list args;
	va_start(args, fmt);
	vfprintf(s_log, fmt, args);
	va_end(args);
	fputc('\n', s_log);
	fflush(s_log);
}

// ---- Print hook ----
static const size_t kMaxCapture = 64 * 1024;

static void Hook_ConsolePrint(void* mgr, const char* str)
{
	if (str && s_captureEnabled && s_captureThread.load() == GetCurrentThreadId())
	{
		if (s_captureBuf.size() < kMaxCapture)
			s_captureBuf += str;
		else if (s_captureBuf.size() == kMaxCapture)
			s_captureBuf += "\n(output truncated at 64 KB)\n";
	}
	s_origPrint(mgr, str);
}

// While one of our lines runs, report the console as open so the game takes the
// same path as a line typed into the ~ console.
static bool Hook_IsMenuOpen(void* ui, void* name)
{
	if (name == s_consoleMenuName && s_captureThread.load() == GetCurrentThreadId())
		return true;
	return s_origIsMenuOpen(ui, name);
}

// Inline detour: overwrite the first `len` (>= 14, whole instructions, no rip-relative
// operands) bytes with an absolute jmp; *orig gets a trampoline that runs them and jumps back.
static bool Detour(unsigned char* target, const unsigned char* expected, size_t len, void* hook, void** orig)
{
	if (memcmp(target, expected, len) != 0)
		return false;

	unsigned char* tramp = (unsigned char*)VirtualAlloc(nullptr, 64, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
	if (!tramp) return false;
	memcpy(tramp, target, len);
	tramp[len] = 0xFF; tramp[len + 1] = 0x25; *(UInt32*)(tramp + len + 2) = 0;   // jmp [rip+0]
	*(UInt64*)(tramp + len + 6) = (UInt64)(target + len);
	*orig = tramp;   // set before the jmp goes live

	DWORD old;
	if (!VirtualProtect(target, len, PAGE_EXECUTE_READWRITE, &old)) { *orig = nullptr; return false; }
	target[0] = 0xFF; target[1] = 0x25; *(UInt32*)(target + 2) = 0;
	*(UInt64*)(target + 6) = (UInt64)hook;
	for (size_t i = 14; i < len; i++) target[i] = 0xCC;
	VirtualProtect(target, len, old, &old);
	FlushInstructionCache(GetCurrentProcess(), target, len);
	return true;
}

// ---- main-thread execution ----
struct ExecRequest
{
	std::vector<std::string> lines;
	std::vector<std::string> outputs;
	std::vector<bool> crashed;
	HANDLE done = nullptr;
	std::atomic<bool> abandoned{ false };
	std::atomic<int> refs{ 2 };   // pipe thread + main thread

	void Release() { if (--refs == 0) { CloseHandle(done); delete this; } }
};

static std::mutex s_pendingLock;
static std::vector<ExecRequest*> s_pending;

// Queue the line the way the console does, drop its echo, then run the queue right away
// so everything the command prints lands in s_captureBuf.
static bool RunLineGuarded(const char* line, void* a, void* b)
{
	__try
	{
		s_execute(line);
		s_captureBuf.clear();
		s_processQueue(a, b);
		return true;
	}
	__except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// Commands that load a save or change worlds run a lot of game logic inside the call; with the console
// reported open, condition functions echo their results, so their output is not captured.
static bool IsWorldChange(const std::string& line)
{
	std::string w;
	for (char ch : line) { if (ch == ' ' || ch == '\t') { if (!w.empty()) break; continue; } w += (char)tolower((unsigned char)ch); }
	return w == "load" || w == "coc" || w == "centeroncell" || w == "cow" || w == "centeronworld"
		|| w == "qqq" || w == "quitgame" || w == "coe" || w == "centeronexterior";
}

static void RunRequest(ExecRequest* r, void* a, void* b)
{
	for (auto& line : r->lines)
	{
		s_captureBuf.clear();
		s_captureEnabled = !IsWorldChange(line);
		s_captureThread.store(GetCurrentThreadId());
		bool ok = RunLineGuarded(line.c_str(), a, b);
		s_captureThread.store(0);
		if (!s_captureEnabled)
			s_captureBuf = "(output not captured for world-changing commands)\n";
		s_captureEnabled = true;
		r->outputs.push_back(s_captureBuf);
		r->crashed.push_back(!ok);
		if (!ok) Log("exception while executing: %s", line.c_str());
	}
}

// Replaces the main loop's per-frame call to the console queue processor.
static void Hook_ProcessQueue(void* a, void* b)
{
	s_processQueue(a, b);   // lines typed into the real console

	std::vector<ExecRequest*> batch;
	{
		std::lock_guard<std::mutex> lock(s_pendingLock);
		batch.swap(s_pending);
	}
	for (auto* r : batch)
	{
		if (!r->abandoned.load())
		{
			RunRequest(r, a, b);
			SetEvent(r->done);
		}
		r->Release();
	}
}

// Point the `call rel32` at `site` to `hook` through a branch-pool stub (must be within 2GB).
static bool PatchCall(F4SETrampolineInterface* tramp, PluginHandle handle, unsigned char* site, uintptr_t expectedTarget, void* hook)
{
	if (site[0] != 0xE8) return false;
	uintptr_t cur = (uintptr_t)(site + 5) + *(int32_t*)(site + 1);
	if (cur != expectedTarget) return false;

	unsigned char* stub = (unsigned char*)tramp->AllocateFromBranchPool(handle, 14);
	if (!stub) return false;
	stub[0] = 0xFF; stub[1] = 0x25; *(UInt32*)(stub + 2) = 0;
	*(UInt64*)(stub + 6) = (UInt64)hook;

	intptr_t rel = (intptr_t)stub - (intptr_t)(site + 5);
	if (rel != (int32_t)rel) return false;
	DWORD old;
	if (!VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old)) return false;
	*(int32_t*)(site + 1) = (int32_t)rel;
	VirtualProtect(site, 5, old, &old);
	FlushInstructionCache(GetCurrentProcess(), site, 5);
	return true;
}

// ---- JSON helpers ----
static void JsonStr(std::string& out, const std::string& s)
{
	out += '"';
	for (unsigned char c : s)
	{
		switch (c)
		{
		case '"': out += "\\\""; break;
		case '\\': out += "\\\\"; break;
		case '\n': out += "\\n"; break;
		case '\r': out += "\\r"; break;
		case '\t': out += "\\t"; break;
		default:
			if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); out += b; }
			else out += (char)c;
		}
	}
	out += '"';
}

static std::string ErrorJson(const std::string& msg)
{
	std::string out = "{\"ok\":false,\"error\":";
	JsonStr(out, msg);
	return out + "}";
}

static std::string HandleRequest(const std::string& req)
{
	if (req == "ping")
	{
		char buf[128];
		sprintf_s(buf, "{\"ok\":true,\"plugin\":\"F4Exec\",\"version\":%d,\"pid\":%lu}", F4EXEC_VERSION, GetCurrentProcessId());
		return buf;
	}

	if (req.compare(0, 5, "exec\n") != 0)
		return ErrorJson("unknown request");

	auto* r = new ExecRequest();
	size_t pos = 5;
	while (pos <= req.size())
	{
		size_t nl = req.find('\n', pos);
		if (nl == std::string::npos) nl = req.size();
		std::string line = req.substr(pos, nl - pos);
		while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
		size_t start = line.find_first_not_of(" \t");
		if (start != std::string::npos && line[start] != ';')
			r->lines.push_back(line.substr(start));
		pos = nl + 1;
	}
	if (r->lines.empty()) { delete r; return ErrorJson("no command"); }

	r->done = CreateEventA(nullptr, TRUE, FALSE, nullptr);
	{
		std::lock_guard<std::mutex> lock(s_pendingLock);
		s_pending.push_back(r);
	}

	// picked up on the next frame; a loading screen or frozen game may delay it
	if (WaitForSingleObject(r->done, 15000) != WAIT_OBJECT_0)
	{
		r->abandoned.store(true);
		r->Release();
		return ErrorJson("timed out waiting for the game's main thread (loading screen or game frozen?)");
	}

	std::string out = "{\"ok\":true,\"results\":[";
	for (size_t i = 0; i < r->lines.size(); i++)
	{
		if (i) out += ',';
		out += "{\"command\":";
		JsonStr(out, r->lines[i]);
		out += ",\"output\":";
		JsonStr(out, r->outputs[i]);
		if (r->crashed[i]) out += ",\"exception\":true";
		out += '}';
	}
	out += "]}";
	r->Release();
	return out;
}

// ---- pipe server ----
static void ServeClient(HANDLE pipe)
{
	for (;;)
	{
		std::string req;
		char buf[4096];
		DWORD read = 0;
		for (;;)
		{
			BOOL ok = ReadFile(pipe, buf, sizeof(buf), &read, nullptr);
			if (ok) { req.append(buf, read); break; }
			if (GetLastError() == ERROR_MORE_DATA) { req.append(buf, read); continue; }
			goto done;
		}

		std::string resp = HandleRequest(req);
		DWORD written = 0;
		if (!WriteFile(pipe, resp.data(), (DWORD)resp.size(), &written, nullptr))
			break;
	}
done:
	FlushFileBuffers(pipe);
	DisconnectNamedPipe(pipe);
	CloseHandle(pipe);
}

static void PipeServerThread()
{
	for (;;)
	{
		HANDLE pipe = CreateNamedPipeA(PIPE_NAME,
			PIPE_ACCESS_DUPLEX,
			PIPE_TYPE_MESSAGE | PIPE_READMODE_MESSAGE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
			PIPE_UNLIMITED_INSTANCES, 1 << 20, 1 << 16, 0, nullptr);
		if (pipe == INVALID_HANDLE_VALUE)
		{
			Log("CreateNamedPipe failed: %lu", GetLastError());
			Sleep(1000);
			continue;
		}
		if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED)
			std::thread(ServeClient, pipe).detach();
		else
			CloseHandle(pipe);
	}
}

// ---- F4SE entry ----
extern "C" {

__declspec(dllexport) F4SEPluginVersionData F4SEPlugin_Version =
{
	F4SEPluginVersionData::kVersion,
	F4EXEC_VERSION,
	"F4Exec",
	"",
	0,
	0,
	{ RUNTIME_VERSION_1_11_240, 0 },
	0,
};

__declspec(dllexport) bool F4SEPlugin_Load(const F4SEInterface* f4se)
{
	char path[MAX_PATH];
	sprintf_s(path, "%s\\Documents\\My Games\\Fallout4\\F4SE\\F4Exec.log", getenv("USERPROFILE"));
	s_log = _fsopen(path, "w", _SH_DENYWR);   // readable by fo4-cli while the game runs
	Log("F4Exec v%d loading, runtime %08X", F4EXEC_VERSION, f4se->runtimeVersion);

	if (f4se->isEditor || f4se->runtimeVersion != RUNTIME_VERSION_1_11_240)
	{
		Log("unsupported runtime");
		return false;
	}

	auto* tramp = (F4SETrampolineInterface*)f4se->QueryInterface(kInterface_Trampoline);
	if (!tramp) { Log("no trampoline interface"); return false; }

	uintptr_t base = (uintptr_t)GetModuleHandleA(nullptr);
	if (memcmp((void*)(base + kRVA_ConsoleExecute), kExecutePrologue, sizeof(kExecutePrologue)) != 0)
	{
		Log("console execute function signature mismatch");
		return false;
	}
	s_execute = (_ConsoleExecute)(base + kRVA_ConsoleExecute);

	s_processQueue = (_ProcessQueue)(base + kRVA_ProcessQueue);
	if (!PatchCall(tramp, f4se->GetPluginHandle(), (unsigned char*)(base + kRVA_ProcessQueueCall),
		base + kRVA_ProcessQueue, (void*)&Hook_ProcessQueue))
	{
		Log("console queue call site mismatch");
		return false;
	}

	s_consoleMenuName = (void*)(base + kRVA_ConsoleMenuName);
	if (!Detour((unsigned char*)(base + kRVA_IsMenuOpen), kIsMenuOpenPrologue, sizeof(kIsMenuOpenPrologue),
		(void*)&Hook_IsMenuOpen, (void**)&s_origIsMenuOpen))
	{
		Log("IsMenuOpen signature mismatch");
		return false;
	}

	if (!Detour((unsigned char*)(base + kRVA_ConsolePrint), kPrintPrologue, sizeof(kPrintPrologue),
		(void*)&Hook_ConsolePrint, (void**)&s_origPrint))
		Log("console print hook failed; output capture disabled");

	std::thread(PipeServerThread).detach();
	Log("listening on %s", PIPE_NAME);
	return true;
}

}
