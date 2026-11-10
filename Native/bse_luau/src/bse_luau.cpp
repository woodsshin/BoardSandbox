#include "bse_luau.h"

#include "json.h"
#include "prelude_data.h"

#include "lua.h"
#include "luacode.h"
#include "lualib.h"

#include <algorithm>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <memory>
#include <string>
#include <unordered_map>

#ifndef BSE_LUAU_VERSION
#define BSE_LUAU_VERSION "unknown"
#endif

namespace
{

constexpr uint32_t kDefaultMemoryMb = 64;
constexpr uint32_t kMaxMemoryMb = 1024;
constexpr uint32_t kDefaultCallTimeoutMs = 250;
constexpr uint32_t kDefaultBootTimeoutMs = 2000;
constexpr size_t kMaxJsonBytes = 16u << 20;
constexpr size_t kMaxSourceBytes = 4u << 20;
constexpr size_t kMaxModules = 4096;
constexpr size_t kMaxModulePathBytes = 200;
constexpr size_t kMaxLogEntries = 2048;
constexpr size_t kMaxLogBytes = 512u << 10;
constexpr size_t kMaxLogLineBytes = 4096;
constexpr uint32_t kInterruptStride = 256;

struct LogEntry
{
    int level;
    std::string text;
};

} // namespace

struct bse_vm
{
    lua_State* L = nullptr;
    lua_State* S = nullptr;
    int thread_ref = LUA_NOREF;
    int dispatch_ref = LUA_NOREF;
    bool prelude_ready = false;
    bool booted = false;
    bool faulted = false;

    uint32_t flags = 0;
    int optimization_level = 1;
    int debug_level = 1;
    uint32_t call_timeout_ms = kDefaultCallTimeoutMs;
    uint32_t boot_timeout_ms = kDefaultBootTimeoutMs;

    size_t memory_used = 0;
    size_t memory_peak = 0;
    size_t memory_limit = 0;

    bool limits_active = false;
    bool timed_out = false;
    bool out_of_memory = false;
    uint32_t active_timeout_ms = 0;
    uint32_t tick = 0;
    std::chrono::steady_clock::time_point deadline;

    std::unordered_map<std::string, std::string> modules;

    std::string result;
    std::string log_text;
    std::deque<LogEntry> logs;
    size_t log_bytes = 0;
    uint64_t dropped_logs = 0;

    uint64_t call_count = 0;
    uint64_t last_call_micros = 0;

    ~bse_vm()
    {
        if (L)
        {
            lua_callbacks(L)->interrupt = nullptr;
            lua_close(L);
            L = nullptr;
        }
    }
};

namespace
{

bse_vm* vm_of(lua_State* L)
{
    return static_cast<bse_vm*>(lua_callbacks(L)->userdata);
}

int32_t fail_with(bse_vm* vm, int32_t status, const std::string& message)
{
    if (vm)
        vm->result = message;
    return status;
}

void* allocator(void* ud, void* ptr, size_t osize, size_t nsize)
{
    bse_vm* vm = static_cast<bse_vm*>(ud);
    if (nsize == 0)
    {
        if (ptr)
        {
            vm->memory_used -= osize;
            std::free(ptr);
        }
        return nullptr;
    }
    size_t old_size = ptr ? osize : 0;
    if (nsize > old_size && vm->memory_limit != 0 && vm->memory_used + (nsize - old_size) > vm->memory_limit)
    {
        vm->out_of_memory = true;
        return nullptr;
    }
    void* block = std::realloc(ptr, nsize);
    if (!block)
    {
        vm->out_of_memory = true;
        return nullptr;
    }
    vm->memory_used = vm->memory_used - old_size + nsize;
    if (vm->memory_used > vm->memory_peak)
        vm->memory_peak = vm->memory_used;
    return block;
}

void interrupt_callback(lua_State* L, int gc)
{
    if (gc >= 0)
        return;
    bse_vm* vm = vm_of(L);
    if (!vm->limits_active)
        return;
    if (!vm->timed_out)
    {
        if (++vm->tick < kInterruptStride)
            return;
        vm->tick = 0;
        if (std::chrono::steady_clock::now() < vm->deadline)
            return;
        vm->timed_out = true;
    }
    lua_rawcheckstack(L, 1);
    luaL_error(L, "script exceeded its time limit");
}

struct LimitScope
{
    bse_vm* vm;

    LimitScope(bse_vm* v, uint32_t timeout_ms) : vm(v)
    {
        vm->timed_out = false;
        vm->out_of_memory = false;
        vm->tick = 0;
        vm->active_timeout_ms = timeout_ms;
        vm->deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
        vm->limits_active = true;
    }

    ~LimitScope() { vm->limits_active = false; }
};

struct FreeGuard
{
    char* p;
    ~FreeGuard() { std::free(p); }
};

void append_log(bse_vm* vm, int level, std::string text)
{
    if (text.size() > kMaxLogLineBytes)
    {
        text.resize(kMaxLogLineBytes);
        text += "...";
    }
    vm->log_bytes += text.size();
    vm->logs.push_back(LogEntry{level, std::move(text)});
    while (vm->logs.size() > kMaxLogEntries || vm->log_bytes > kMaxLogBytes)
    {
        vm->log_bytes -= vm->logs.front().text.size();
        vm->logs.pop_front();
        vm->dropped_logs++;
    }
}

std::string join_arguments(lua_State* L, int first)
{
    std::string line;
    int n = lua_gettop(L);
    for (int i = first; i <= n; ++i)
    {
        size_t len = 0;
        const char* s = luaL_tolstring(L, i, &len);
        if (i > first)
            line.push_back('\t');
        if (line.size() < kMaxLogLineBytes)
            line.append(s, len);
        lua_pop(L, 1);
    }
    return line;
}

int host_print(lua_State* L)
{
    append_log(vm_of(L), 0, join_arguments(L, 1));
    return 0;
}

int native_log(lua_State* L)
{
    int level = luaL_checkinteger(L, 1);
    append_log(vm_of(L), level, join_arguments(L, 2));
    return 0;
}

int native_encode(lua_State* L)
{
    luaL_checkany(L, 1);
    std::string out;
    std::string err;
    if (!bse::json_encode(L, 1, out, err, kMaxJsonBytes))
        luaL_error(L, "json.encode: %s", err.c_str());
    lua_pushlstring(L, out.data(), out.size());
    return 1;
}

int native_decode(lua_State* L)
{
    size_t n = 0;
    const char* s = luaL_checklstring(L, 1, &n);
    if (n > kMaxJsonBytes)
        luaL_error(L, "json.decode: input is too large");
    std::string err;
    if (!bse::json_decode(L, s, n, err))
        luaL_error(L, "json.decode: %s", err.c_str());
    return 1;
}

bool compile_and_load(lua_State* L, bse_vm* vm, const char* chunkname, const char* source, size_t size)
{
    lua_CompileOptions options = {};
    options.optimizationLevel = vm->optimization_level;
    options.debugLevel = vm->debug_level;
    size_t bytecode_size = 0;
    char* bytecode = luau_compile(source, size, &options, &bytecode_size);
    if (!bytecode)
    {
        lua_pushstring(L, "out of memory while compiling");
        return false;
    }
    FreeGuard guard{bytecode};
    return luau_load(L, chunkname, bytecode, bytecode_size, 0) == 0;
}

int native_load(lua_State* L)
{
    size_t n = 0;
    const char* p = luaL_checklstring(L, 1, &n);
    bse_vm* vm = vm_of(L);
    std::string path(p, n);
    auto it = vm->modules.find(path);
    if (it == vm->modules.end())
    {
        lua_pushnil(L);
        lua_pushfstring(L, "module '%s' was not found", path.c_str());
        return 2;
    }
    std::string chunk = "@" + path;
    if (!compile_and_load(L, vm, chunk.c_str(), it->second.data(), it->second.size()))
    {
        lua_pushnil(L);
        lua_insert(L, -2);
        return 2;
    }
    return 1;
}

int native_loadstring(lua_State* L)
{
    bse_vm* vm = vm_of(L);
    if ((vm->flags & BSE_FLAG_DEV) == 0)
        luaL_error(L, "loadstring is only available in dev mode");
    size_t name_len = 0;
    const char* name = luaL_checklstring(L, 1, &name_len);
    size_t n = 0;
    const char* source = luaL_checklstring(L, 2, &n);
    if (n > kMaxSourceBytes)
        luaL_error(L, "source is too large");
    std::string chunk = "=" + std::string(name, name_len);
    if (!compile_and_load(L, vm, chunk.c_str(), source, n))
    {
        lua_pushnil(L);
        lua_insert(L, -2);
        return 2;
    }
    return 1;
}

int native_exists(lua_State* L)
{
    size_t n = 0;
    const char* p = luaL_checklstring(L, 1, &n);
    lua_pushboolean(L, vm_of(L)->modules.count(std::string(p, n)) != 0);
    return 1;
}

int init_state(lua_State* L)
{
    bse_vm* vm = static_cast<bse_vm*>(lua_touserdata(L, 1));
    luaL_openlibs(L);

    static const char* const removed[] = {"os", "getfenv", "setfenv"};
    for (const char* name : removed)
    {
        lua_pushnil(L);
        lua_setglobal(L, name);
    }
    lua_pushcfunction(L, host_print, "print");
    lua_setglobal(L, "print");

    luaL_sandbox(L);

    lua_State* thread = lua_newthread(L);
    vm->thread_ref = lua_ref(L, -1);
    lua_pop(L, 1);
    luaL_sandboxthread(thread);
    vm->S = thread;
    return 0;
}

struct InstallContext
{
    explicit InstallContext(bse_vm* v) : vm(v) {}

    bse_vm* vm;
    bool failed = false;
    std::string error;
};

int install_prelude(lua_State* L)
{
    InstallContext* ctx = static_cast<InstallContext*>(lua_touserdata(L, 1));
    bse_vm* vm = ctx->vm;

    lua_createtable(L, 0, 10);
    const luaL_Reg functions[] = {
        {"log", native_log},
        {"encode", native_encode},
        {"decode", native_decode},
        {"load", native_load},
        {"loadstring", native_loadstring},
        {"exists", native_exists},
        {nullptr, nullptr},
    };
    for (const luaL_Reg* f = functions; f->name; ++f)
    {
        lua_pushcfunction(L, f->func, f->name);
        lua_setfield(L, -2, f->name);
    }
    lua_pushlightuserdata(L, &bse::json_null_tag);
    lua_setfield(L, -2, "null");
    lua_pushboolean(L, (vm->flags & BSE_FLAG_DEV) != 0);
    lua_setfield(L, -2, "dev");
    lua_pushstring(L, BSE_LUAU_VERSION);
    lua_setfield(L, -2, "luau_version");

    const char* prelude = reinterpret_cast<const char*>(bse::kPreludeData);
    if (!compile_and_load(L, vm, "=bse/prelude", prelude, bse::kPreludeSize))
    {
        ctx->failed = true;
        const char* message = lua_tostring(L, -1);
        ctx->error = std::string("prelude failed to load: ") + (message ? message : "unknown error");
        return 0;
    }
    lua_pushvalue(L, 2);
    lua_call(L, 1, 1);
    if (!lua_istable(L, -1))
    {
        ctx->failed = true;
        ctx->error = "prelude did not return a table";
        return 0;
    }

    lua_getfield(L, -1, "bse");
    lua_setglobal(L, "bse");
    lua_getfield(L, -1, "require");
    lua_setglobal(L, "require");
    lua_getfield(L, -1, "dispatch");
    if (!lua_isfunction(L, -1))
    {
        ctx->failed = true;
        ctx->error = "prelude did not return a dispatcher";
        return 0;
    }
    vm->dispatch_ref = lua_ref(L, -1);
    lua_pop(L, 1);

    lua_setreadonly(L, LUA_GLOBALSINDEX, 1);
    lua_setsafeenv(L, LUA_GLOBALSINDEX, 1);
    return 0;
}

struct CallContext
{
    CallContext(bse_vm* v, const char* m, size_t m_len, const char* a, size_t a_len, bool is_boot)
        : vm(v), method(m), method_len(m_len), args(a), args_len(a_len), boot(is_boot)
    {
    }

    bse_vm* vm;
    const char* method;
    size_t method_len;
    const char* args;
    size_t args_len;
    bool boot;
    int32_t status = BSE_OK;
    std::string error;
};

void read_boot_failure(lua_State* L, CallContext* ctx)
{
    std::string text = "boot failed";
    int32_t code = BSE_ERR_RUNTIME;
    lua_getfield(L, -1, "error");
    if (lua_istable(L, -1))
    {
        lua_getfield(L, -1, "message");
        if (lua_type(L, -1) == LUA_TSTRING)
            text = lua_tostring(L, -1);
        lua_pop(L, 1);
        lua_getfield(L, -1, "traceback");
        if (lua_type(L, -1) == LUA_TSTRING)
            text += std::string("\n") + lua_tostring(L, -1);
        lua_pop(L, 1);
        lua_getfield(L, -1, "code");
        if (lua_type(L, -1) == LUA_TSTRING && std::strcmp(lua_tostring(L, -1), "COMPILE_ERROR") == 0)
            code = BSE_ERR_COMPILE;
        lua_pop(L, 1);
    }
    lua_pop(L, 1);
    ctx->status = code;
    ctx->error = text;
}

int dispatch_call(lua_State* L)
{
    CallContext* ctx = static_cast<CallContext*>(lua_touserdata(L, 1));
    bse_vm* vm = ctx->vm;

    lua_getref(L, vm->dispatch_ref);
    lua_pushlstring(L, ctx->method, ctx->method_len);
    if (ctx->args_len > 0)
    {
        std::string err;
        if (!bse::json_decode(L, ctx->args, ctx->args_len, err))
        {
            ctx->status = BSE_ERR_JSON;
            ctx->error = "arguments: " + err;
            return 0;
        }
    }
    else
    {
        lua_pushnil(L);
    }
    lua_call(L, 2, 1);

    if (ctx->boot)
    {
        lua_getfield(L, -1, "ok");
        bool ok = lua_toboolean(L, -1) != 0;
        lua_pop(L, 1);
        if (!ok)
        {
            read_boot_failure(L, ctx);
            return 0;
        }
    }

    std::string out;
    std::string err;
    if (!bse::json_encode(L, -1, out, err, kMaxJsonBytes))
    {
        ctx->status = BSE_ERR_JSON;
        ctx->error = "result: " + err;
        return 0;
    }
    vm->result = std::move(out);
    return 0;
}

int32_t finish_call(bse_vm* vm, int lua_status, CallContext& ctx)
{
    if (vm->timed_out)
    {
        vm->faulted = true;
        lua_settop(vm->S, 0);
        return fail_with(vm, BSE_ERR_TIMEOUT, "script exceeded its time limit of " + std::to_string(vm->active_timeout_ms) + " ms");
    }
    if (vm->out_of_memory || lua_status == LUA_ERRMEM)
    {
        vm->faulted = true;
        lua_settop(vm->S, 0);
        return fail_with(vm, BSE_ERR_MEMORY, "script exceeded its memory limit");
    }
    if (lua_status == LUA_OK)
    {
        lua_settop(vm->S, 0);
        if (ctx.status != BSE_OK)
            return fail_with(vm, ctx.status, ctx.error);
        return BSE_OK;
    }
    const char* message = lua_type(vm->S, -1) == LUA_TSTRING ? lua_tostring(vm->S, -1) : nullptr;
    std::string text = message ? message : "the script raised a non-string error";
    lua_settop(vm->S, 0);
    return fail_with(vm, BSE_ERR_RUNTIME, text);
}

int32_t run_call(bse_vm* vm, const char* method, size_t method_len, const char* args, size_t args_len, uint32_t timeout_ms, bool boot)
{
    CallContext ctx(vm, method, method_len, args, args_len, boot);
    vm->result.clear();
    auto started = std::chrono::steady_clock::now();
    int lua_status;
    {
        LimitScope scope(vm, timeout_ms);
        lua_status = lua_cpcall(vm->S, dispatch_call, &ctx);
    }
    vm->last_call_micros = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - started).count());
    vm->call_count++;
    return finish_call(vm, lua_status, ctx);
}

uint32_t clamp_option(int32_t value, int32_t fallback, int32_t max_value)
{
    if (value < 0)
        return static_cast<uint32_t>(fallback);
    return static_cast<uint32_t>(std::min(value, max_value));
}

} // namespace

extern "C"
{

uint32_t bse_abi_version(void)
{
    return BSE_ABI_VERSION;
}

const char* bse_luau_version(void)
{
    return BSE_LUAU_VERSION;
}

bse_vm* bse_vm_create(const bse_options* options)
{
    try
    {
        std::unique_ptr<bse_vm> vm(new bse_vm());
        uint32_t memory_mb = kDefaultMemoryMb;
        if (options)
        {
            if (options->struct_size != sizeof(bse_options))
                return nullptr;
            vm->flags = options->flags;
            if (options->memory_limit_mb != 0)
                memory_mb = std::min(options->memory_limit_mb, kMaxMemoryMb);
            if (options->call_timeout_ms != 0)
                vm->call_timeout_ms = options->call_timeout_ms;
            if (options->boot_timeout_ms != 0)
                vm->boot_timeout_ms = options->boot_timeout_ms;
            vm->optimization_level = static_cast<int>(clamp_option(options->optimization_level, 1, 2));
            vm->debug_level = static_cast<int>(clamp_option(options->debug_level, 1, 2));
        }
        vm->memory_limit = static_cast<size_t>(memory_mb) << 20;

        vm->L = lua_newstate(allocator, vm.get());
        if (!vm->L)
            return nullptr;
        lua_callbacks(vm->L)->userdata = vm.get();
        lua_callbacks(vm->L)->interrupt = interrupt_callback;
        if (lua_cpcall(vm->L, init_state, vm.get()) != LUA_OK)
            return nullptr;
        return vm.release();
    }
    catch (...)
    {
        return nullptr;
    }
}

void bse_vm_destroy(bse_vm* vm)
{
    try
    {
        delete vm;
    }
    catch (...)
    {
    }
}

int32_t bse_vm_add_module(bse_vm* vm, const char* path, int32_t path_len, const char* source, int32_t source_len)
{
    try
    {
        if (!vm || !path || path_len <= 0 || static_cast<size_t>(path_len) > kMaxModulePathBytes || source_len < 0 || (source_len > 0 && !source))
            return fail_with(vm, BSE_ERR_ARGUMENT, "invalid module arguments");
        if (vm->booted || vm->prelude_ready)
            return fail_with(vm, BSE_ERR_STATE, "modules must be added before boot");
        if (static_cast<size_t>(source_len) > kMaxSourceBytes)
            return fail_with(vm, BSE_ERR_ARGUMENT, "module source is too large");
        std::string key(path, static_cast<size_t>(path_len));
        if (key.find('\0') != std::string::npos)
            return fail_with(vm, BSE_ERR_ARGUMENT, "module path contains a NUL character");
        if (vm->modules.size() >= kMaxModules && vm->modules.find(key) == vm->modules.end())
            return fail_with(vm, BSE_ERR_ARGUMENT, "too many modules");
        vm->modules[key].assign(source ? source : "", static_cast<size_t>(source_len));
        return BSE_OK;
    }
    catch (...)
    {
        return BSE_ERR_INTERNAL;
    }
}

int32_t bse_vm_boot(bse_vm* vm, const char* entry, int32_t entry_len)
{
    try
    {
        if (!vm || !entry || entry_len <= 0)
            return fail_with(vm, BSE_ERR_ARGUMENT, "invalid boot arguments");
        if (vm->faulted)
            return fail_with(vm, BSE_ERR_FAULTED, "the VM is faulted and must be recreated");
        if (vm->booted || vm->prelude_ready)
            return fail_with(vm, BSE_ERR_STATE, "the VM is already booted");
        std::string entry_path(entry, static_cast<size_t>(entry_len));
        if (vm->modules.find(entry_path) == vm->modules.end())
            return fail_with(vm, BSE_ERR_ARGUMENT, "entry module '" + entry_path + "' was not added");
        std::string args = "{\"entry\":";
        if (!bse::json_append_string(args, entry_path.data(), entry_path.size()))
            return fail_with(vm, BSE_ERR_ARGUMENT, "entry path is not valid UTF-8");
        args.push_back('}');

        vm->result.clear();
        InstallContext install(vm);
        int lua_status;
        {
            LimitScope scope(vm, vm->boot_timeout_ms);
            lua_status = lua_cpcall(vm->S, install_prelude, &install);
        }
        if (vm->timed_out || lua_status != LUA_OK || install.failed)
        {
            vm->faulted = true;
            std::string message = install.failed ? install.error : "the runtime prelude failed to initialize";
            if (lua_status != LUA_OK && lua_type(vm->S, -1) == LUA_TSTRING)
                message += std::string(": ") + lua_tostring(vm->S, -1);
            lua_settop(vm->S, 0);
            return fail_with(vm, BSE_ERR_INTERNAL, message);
        }
        lua_settop(vm->S, 0);
        vm->prelude_ready = true;

        int32_t rc = run_call(vm, "boot", 4, args.data(), args.size(), vm->boot_timeout_ms, true);
        if (rc == BSE_OK)
            vm->booted = true;
        else
            vm->faulted = true;
        return rc;
    }
    catch (...)
    {
        return BSE_ERR_INTERNAL;
    }
}

int32_t bse_vm_call(bse_vm* vm, const char* method, int32_t method_len, const char* args_json, int32_t args_len, uint32_t timeout_ms)
{
    try
    {
        if (!vm || !method || method_len <= 0 || args_len < 0 || (args_len > 0 && !args_json))
            return fail_with(vm, BSE_ERR_ARGUMENT, "invalid call arguments");
        if (static_cast<size_t>(args_len) > kMaxJsonBytes)
            return fail_with(vm, BSE_ERR_ARGUMENT, "arguments are too large");
        if (vm->faulted)
            return fail_with(vm, BSE_ERR_FAULTED, "the VM is faulted and must be recreated");
        if (!vm->booted)
            return fail_with(vm, BSE_ERR_STATE, "the VM is not booted");
        return run_call(vm, method, static_cast<size_t>(method_len), args_json, static_cast<size_t>(args_len),
            timeout_ms != 0 ? timeout_ms : vm->call_timeout_ms, false);
    }
    catch (...)
    {
        return BSE_ERR_INTERNAL;
    }
}

const char* bse_vm_result(bse_vm* vm, int32_t* out_len)
{
    if (!vm)
    {
        if (out_len)
            *out_len = 0;
        return "";
    }
    if (out_len)
        *out_len = static_cast<int32_t>(vm->result.size());
    return vm->result.c_str();
}

const char* bse_vm_take_logs(bse_vm* vm, int32_t* out_len)
{
    try
    {
        if (!vm)
        {
            if (out_len)
                *out_len = 0;
            return "";
        }
        std::string& out = vm->log_text;
        out.clear();
        out.push_back('[');
        bool first = true;
        for (const LogEntry& entry : vm->logs)
        {
            if (!first)
                out.push_back(',');
            first = false;
            out += "{\"l\":";
            out += std::to_string(entry.level);
            out += ",\"m\":";
            bse::json_append_string_lossy(out, entry.text.data(), entry.text.size());
            out.push_back('}');
        }
        out.push_back(']');
        vm->logs.clear();
        vm->log_bytes = 0;
        if (out_len)
            *out_len = static_cast<int32_t>(out.size());
        return out.c_str();
    }
    catch (...)
    {
        if (out_len)
            *out_len = 0;
        return "";
    }
}

int32_t bse_vm_get_stats(bse_vm* vm, bse_stats* out)
{
    if (!vm || !out || out->struct_size != sizeof(bse_stats))
        return BSE_ERR_ARGUMENT;
    out->faulted = vm->faulted ? 1u : 0u;
    out->memory_bytes = vm->memory_used;
    out->memory_peak_bytes = vm->memory_peak;
    out->call_count = vm->call_count;
    out->last_call_micros = vm->last_call_micros;
    out->dropped_logs = vm->dropped_logs;
    return BSE_OK;
}

} // extern "C"
