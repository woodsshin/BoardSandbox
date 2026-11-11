#include "bse_luau.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if defined(_WIN32)
#include <windows.h>
#else
#include <pthread.h>
#include <unistd.h>
#endif

#ifndef BSE_TEST_DIR
#define BSE_TEST_DIR "tests/luau"
#endif

static int g_checks;
static int g_failures;
static const char* g_section = "";
static const char* g_dir = BSE_TEST_DIR;
static char* g_out;

static void fail_at(const char* file, int line)
{
    ++g_failures;
    fprintf(stderr, "FAIL [%s] %s:%d\n", g_section, file, line);
}

#define CHECK(cond)                                  \
    do                                               \
    {                                                \
        ++g_checks;                                  \
        if (!(cond))                                 \
        {                                            \
            fail_at(__FILE__, __LINE__);             \
            fprintf(stderr, "  condition: %s\n", #cond); \
        }                                            \
    } while (0)

static void check_int(long long actual, long long expected, const char* expr, const char* file, int line)
{
    ++g_checks;
    if (actual != expected)
    {
        fail_at(file, line);
        fprintf(stderr, "  %s\n  expected: %lld\n  actual:   %lld\n", expr, expected, actual);
    }
}

static void check_str(const char* actual, const char* expected, const char* file, int line)
{
    ++g_checks;
    if (!actual || strcmp(actual, expected) != 0)
    {
        fail_at(file, line);
        fprintf(stderr, "  expected: %s\n  actual:   %s\n", expected, actual ? actual : "(null)");
    }
}

static void check_has(const char* haystack, const char* needle, const char* file, int line)
{
    ++g_checks;
    if (!haystack || !strstr(haystack, needle))
    {
        fail_at(file, line);
        fprintf(stderr, "  expected to contain: %s\n  actual: %.600s\n", needle, haystack ? haystack : "(null)");
    }
}

static void check_lacks(const char* haystack, const char* needle, const char* file, int line)
{
    ++g_checks;
    if (haystack && strstr(haystack, needle))
    {
        fail_at(file, line);
        fprintf(stderr, "  expected not to contain: %s\n  actual: %.600s\n", needle, haystack);
    }
}

#define CHECK_INT(actual, expected) check_int((long long)(actual), (long long)(expected), #actual, __FILE__, __LINE__)
#define CHECK_STR(actual, expected) check_str((actual), (expected), __FILE__, __LINE__)
#define CHECK_HAS(haystack, needle) check_has((haystack), (needle), __FILE__, __LINE__)
#define CHECK_LACKS(haystack, needle) check_lacks((haystack), (needle), __FILE__, __LINE__)

static double now_ms(void)
{
    struct timespec ts;
    timespec_get(&ts, TIME_UTC);
    return (double)ts.tv_sec * 1000.0 + (double)ts.tv_nsec / 1e6;
}

static char* copy_text(const char* text, size_t n)
{
    char* copy = (char*)malloc(n + 1);
    if (!copy)
    {
        fprintf(stderr, "out of memory in test harness\n");
        exit(2);
    }
    memcpy(copy, text, n);
    copy[n] = 0;
    return copy;
}

static char* slurp(const char* relative, size_t* length)
{
    char path[1024];
    snprintf(path, sizeof(path), "%s/%s", g_dir, relative);
    FILE* f = fopen(path, "rb");
    if (!f)
    {
        fprintf(stderr, "cannot open %s\n", path);
        exit(2);
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    char* data = (char*)malloc((size_t)size + 1);
    size_t got = fread(data, 1, (size_t)size, f);
    fclose(f);
    if (got != (size_t)size)
    {
        fprintf(stderr, "short read on %s\n", path);
        exit(2);
    }
    data[size] = 0;
    *length = (size_t)size;
    return data;
}

static bse_options make_options(void)
{
    bse_options options;
    memset(&options, 0, sizeof(options));
    options.struct_size = (uint32_t)sizeof(bse_options);
    options.optimization_level = -1;
    options.debug_level = -1;
    return options;
}

static bse_vm* new_vm(const bse_options* options)
{
    bse_vm* vm = bse_vm_create(options);
    if (!vm)
    {
        fprintf(stderr, "bse_vm_create failed\n");
        exit(2);
    }
    return vm;
}

static void add_file(bse_vm* vm, const char* module_path, const char* file)
{
    size_t n = 0;
    char* source = slurp(file, &n);
    int32_t rc = bse_vm_add_module(vm, module_path, (int32_t)strlen(module_path), source, (int32_t)n);
    free(source);
    if (rc != BSE_OK)
    {
        fprintf(stderr, "add_module(%s) failed: %d\n", module_path, rc);
        exit(2);
    }
}

static void add_text(bse_vm* vm, const char* module_path, const char* text)
{
    int32_t rc = bse_vm_add_module(vm, module_path, (int32_t)strlen(module_path), text, (int32_t)strlen(text));
    if (rc != BSE_OK)
    {
        fprintf(stderr, "add_module(%s) failed: %d\n", module_path, rc);
        exit(2);
    }
}

static int boot_entry(bse_vm* vm, const char* entry)
{
    return bse_vm_boot(vm, entry, (int32_t)strlen(entry));
}

static const char* last_result(bse_vm* vm)
{
    int32_t n = 0;
    return bse_vm_result(vm, &n);
}

static bse_vm* open_vm(const char* file, const bse_options* options)
{
    bse_vm* vm = new_vm(options);
    add_file(vm, "main.luau", file);
    int rc = boot_entry(vm, "main.luau");
    if (rc != BSE_OK)
    {
        fprintf(stderr, "boot of %s failed (%d): %s\n", file, rc, last_result(vm));
        exit(2);
    }
    return vm;
}

static int call(bse_vm* vm, const char* method, const char* args)
{
    int32_t n = 0;
    int status = bse_vm_call(vm, method, (int32_t)strlen(method), args, args ? (int32_t)strlen(args) : 0, 0);
    const char* result = bse_vm_result(vm, &n);
    free(g_out);
    g_out = copy_text(result, (size_t)n);
    return status;
}

static void expect_call(bse_vm* vm, const char* method, const char* args, const char* expected, const char* file, int line)
{
    int status = call(vm, method, args);
    ++g_checks;
    if (status != BSE_OK || strcmp(g_out, expected) != 0)
    {
        fail_at(file, line);
        fprintf(stderr, "  call %s %s\n  status: %d\n  expected: %s\n  actual:   %s\n", method, args ? args : "(null)", status, expected, g_out);
    }
}

static void expect_status(bse_vm* vm, const char* method, const char* args, int expected, const char* file, int line)
{
    int status = call(vm, method, args);
    ++g_checks;
    if (status != expected)
    {
        fail_at(file, line);
        fprintf(stderr, "  call %s %s\n  expected status: %d\n  actual status:   %d\n  result: %.400s\n", method, args ? args : "(null)", expected, status, g_out);
    }
}

#define EXPECT_CALL(vm, method, args, expected) expect_call((vm), (method), (args), (expected), __FILE__, __LINE__)
#define EXPECT_STATUS(vm, method, args, expected) expect_status((vm), (method), (args), (expected), __FILE__, __LINE__)

static void expect_fault(bse_vm* vm, const char* method, const char* args, const char* code, const char* fragment, const char* file, int line)
{
    int status = call(vm, method, args);
    ++g_checks;
    char code_needle[128];
    snprintf(code_needle, sizeof(code_needle), "\"code\":\"%s\"", code);
    int ok = status == BSE_OK && strstr(g_out, "\"fault\":true") && strstr(g_out, "\"ok\":false") && strstr(g_out, code_needle) &&
             (!fragment || strstr(g_out, fragment));
    if (!ok)
    {
        fail_at(file, line);
        fprintf(stderr, "  call %s %s\n  expected fault %s containing %s\n  status: %d\n  actual: %.600s\n", method, args ? args : "(null)", code,
            fragment ? fragment : "(any)", status, g_out);
    }
}

#define EXPECT_FAULT(vm, method, args, code, fragment) expect_fault((vm), (method), (args), (code), (fragment), __FILE__, __LINE__)

static bse_stats get_stats(bse_vm* vm)
{
    bse_stats stats;
    memset(&stats, 0, sizeof(stats));
    stats.struct_size = (uint32_t)sizeof(bse_stats);
    int32_t rc = bse_vm_get_stats(vm, &stats);
    CHECK(rc == BSE_OK);
    return stats;
}

static const char* take_logs(bse_vm* vm)
{
    int32_t n = 0;
    const char* logs = bse_vm_take_logs(vm, &n);
    free(g_out);
    g_out = copy_text(logs, (size_t)n);
    return g_out;
}

static size_t count_of(const char* haystack, const char* needle)
{
    size_t count = 0;
    size_t step = strlen(needle);
    for (const char* p = strstr(haystack, needle); p; p = strstr(p + step, needle))
        ++count;
    return count;
}

static char* snapshot_argument(const char* snapshot_result)
{
    const char* key = strstr(snapshot_result, "\"snapshot\":");
    if (!key)
        return NULL;
    const char* literal = key + strlen("\"snapshot\":");
    size_t length = strlen(literal);
    if (length == 0 || literal[length - 1] != '}')
        return NULL;
    size_t n = length - 1;
    char* args = (char*)malloc(n + 32);
    snprintf(args, n + 32, "{\"snapshot\":%.*s}", (int)n, literal);
    return args;
}

#define SECTION(name) (g_section = (name))

static void test_abi(void)
{
    SECTION("abi");
    CHECK_INT(bse_abi_version(), BSE_ABI_VERSION);
    CHECK_STR(bse_luau_version(), "0.745");

    bse_options bad = make_options();
    bad.struct_size = 3;
    CHECK(bse_vm_create(&bad) == NULL);

    bse_vm* vm = bse_vm_create(NULL);
    CHECK(vm != NULL);

    bse_stats stats;
    memset(&stats, 0, sizeof(stats));
    stats.struct_size = 1;
    CHECK_INT(bse_vm_get_stats(vm, &stats), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_get_stats(NULL, &stats), BSE_ERR_ARGUMENT);
    stats.struct_size = (uint32_t)sizeof(stats);
    CHECK_INT(bse_vm_get_stats(vm, NULL), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_get_stats(vm, &stats), BSE_OK);
    CHECK_INT(stats.faulted, 0);
    CHECK(stats.memory_bytes > 0);

    CHECK_INT(bse_vm_call(vm, "ping", 4, NULL, 0, 0), BSE_ERR_STATE);
    CHECK_INT(bse_vm_call(NULL, "ping", 4, NULL, 0, 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_call(vm, NULL, 4, NULL, 0, 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_call(vm, "ping", 0, NULL, 0, 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_call(vm, "ping", 4, NULL, 5, 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_call(vm, "ping", 4, "{}", -1, 0), BSE_ERR_ARGUMENT);

    CHECK_INT(bse_vm_add_module(NULL, "a.luau", 6, "", 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, NULL, 6, "", 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, "a.luau", 0, "", 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, "a.luau", 6, "x", -1), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, "a.luau", 6, NULL, 3), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, "a\0b", 3, "", 0), BSE_ERR_ARGUMENT);
    char longest[201];
    memset(longest, 'a', sizeof(longest));
    CHECK_INT(bse_vm_add_module(vm, longest, 200, "", 0), BSE_OK);
    CHECK_INT(bse_vm_add_module(vm, longest, 201, "", 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_add_module(vm, "empty.luau", 10, NULL, 0), BSE_OK);

    CHECK_INT(boot_entry(vm, "missing.luau"), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_boot(vm, NULL, 3), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_boot(vm, "x", 0), BSE_ERR_ARGUMENT);
    CHECK_INT(bse_vm_boot(NULL, "x", 1), BSE_ERR_ARGUMENT);

    int32_t n = -1;
    CHECK_STR(bse_vm_result(NULL, &n), "");
    CHECK_INT(n, 0);
    n = -1;
    CHECK_STR(bse_vm_take_logs(NULL, &n), "");
    CHECK_INT(n, 0);
    bse_vm_destroy(NULL);
    bse_vm_destroy(vm);
}

static void test_lifecycle(void)
{
    SECTION("lifecycle");
    bse_vm* vm = open_vm("basic.luau", NULL);

    CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_STATE);
    CHECK_INT(bse_vm_add_module(vm, "late.luau", 9, "", 0), BSE_ERR_STATE);

    EXPECT_CALL(vm, "ping", NULL, "{\"luau\":\"0.745\",\"ok\":true}");
    EXPECT_CALL(vm, "ping", "{}", "{\"luau\":\"0.745\",\"ok\":true}");
    EXPECT_CALL(vm, "ping", "null", "{\"luau\":\"0.745\",\"ok\":true}");
    EXPECT_CALL(vm, "nope", NULL, "{\"error\":{\"code\":\"NO_SUCH_METHOD\",\"message\":\"unknown method 'nope'\"},\"ok\":false}");

    EXPECT_CALL(vm, "new_game", "{}", "{\"events\":[{\"t\":\"reset\"},{\"seat\":1,\"t\":\"turn\"}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":3,\"y\":4}}",
        "{\"events\":[{\"id\":1,\"kind\":\"stone\",\"owner\":1,\"t\":\"spawn\",\"x\":3,\"y\":4},{\"seat\":2,\"t\":\"turn\"}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":2,\"cmd\":{\"action\":\"place\",\"x\":5,\"y\":6}}",
        "{\"events\":[{\"id\":2,\"kind\":\"stone\",\"owner\":2,\"t\":\"spawn\",\"x\":5,\"y\":6},{\"seat\":1,\"t\":\"turn\"}],\"ok\":true}");

    EXPECT_CALL(vm, "query", "{\"name\":\"counter\"}", "{\"ok\":true,\"value\":2}");
    EXPECT_CALL(vm, "query", "{\"name\":\"session\"}", "{\"ok\":true,\"value\":{\"turn\":1}}");
    EXPECT_CALL(vm, "query", "{\"name\":\"view\"}",
        "{\"ok\":true,\"value\":[{\"id\":1,\"kind\":\"stone\",\"owner\":1,\"x\":3,\"y\":4},{\"id\":2,\"kind\":\"stone\",\"owner\":2,\"x\":5,\"y\":6}]}");
    EXPECT_CALL(vm, "query", "{\"name\":\"unknown\"}", "{\"ok\":true}");
    EXPECT_FAULT(vm, "query", "{}", "BAD_ARGUMENT", "name must be a string");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"fail\"}}", "{\"error\":{\"code\":\"NOPE\",\"message\":\"not allowed\"},\"ok\":false}");
    EXPECT_CALL(vm, "query", "{\"name\":\"counter\"}", "{\"ok\":true,\"value\":2}");

    EXPECT_FAULT(vm, "command", "{\"seat\":\"x\",\"cmd\":{}}", "BAD_ARGUMENT", "seat must be a number");
    EXPECT_FAULT(vm, "command", "{\"seat\":1}", "BAD_ARGUMENT", "cmd must be an object");
    EXPECT_FAULT(vm, "command", "{\"cmd\":{}}", "BAD_ARGUMENT", "seat must be a number");

    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"boom\"}}", "SCRIPT_ERROR", "boom");
    CHECK_HAS(g_out, "\"traceback\":");
    CHECK_HAS(g_out, "main.luau:");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"boom_table\"}}", "CUSTOM", "custom failure");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"reject_after_emit\"}}", "INVALID_REJECTION", "emitted events");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":9,\"y\":9}}",
        "{\"events\":[{\"id\":3,\"kind\":\"stone\",\"owner\":1,\"t\":\"spawn\",\"x\":9,\"y\":9},{\"seat\":2,\"t\":\"turn\"}],\"ok\":true}");

    bse_stats stats = get_stats(vm);
    CHECK_INT(stats.faulted, 0);
    CHECK(stats.call_count >= 10);
    CHECK(stats.memory_peak_bytes >= stats.memory_bytes);

    bse_vm_destroy(vm);
}

static void test_snapshot(void)
{
    SECTION("snapshot");
    bse_vm* vm = open_vm("basic.luau", NULL);
    EXPECT_CALL(vm, "new_game", "{}", "{\"events\":[{\"t\":\"reset\"},{\"seat\":1,\"t\":\"turn\"}],\"ok\":true}");
    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":3,\"y\":4}}", BSE_OK);
    EXPECT_STATUS(vm, "command", "{\"seat\":2,\"cmd\":{\"action\":\"place\",\"x\":5,\"y\":6}}", BSE_OK);

    EXPECT_STATUS(vm, "snapshot", NULL, BSE_OK);
    CHECK_STR(g_out,
        "{\"ok\":true,\"snapshot\":\"{\\\"mod\\\":{\\\"counter\\\":2},\\\"next_id\\\":3,\\\"turn\\\":1,\\\"v\\\":1,\\\"view\\\":[{\\\"id\\\":1,\\\"kind\\\":"
        "\\\"stone\\\",\\\"owner\\\":1,\\\"x\\\":3,\\\"y\\\":4},{\\\"id\\\":2,\\\"kind\\\":\\\"stone\\\",\\\"owner\\\":2,\\\"x\\\":5,\\\"y\\\":6}]}\"}");
    char* restore_args = snapshot_argument(g_out);
    CHECK(restore_args != NULL);

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":1,\"y\":1}}", BSE_OK);
    EXPECT_CALL(vm, "query", "{\"name\":\"counter\"}", "{\"ok\":true,\"value\":3}");

    EXPECT_CALL(vm, "restore", restore_args,
        "{\"events\":[{\"t\":\"reset\"},{\"id\":1,\"kind\":\"stone\",\"owner\":1,\"t\":\"spawn\",\"x\":3,\"y\":4},{\"id\":2,\"kind\":\"stone\",\"owner\":2,\"t\":\"spawn\","
        "\"x\":5,\"y\":6},{\"seat\":1,\"t\":\"turn\"}],\"ok\":true}");
    EXPECT_CALL(vm, "query", "{\"name\":\"counter\"}", "{\"ok\":true,\"value\":2}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":7,\"y\":7}}",
        "{\"events\":[{\"id\":3,\"kind\":\"stone\",\"owner\":1,\"t\":\"spawn\",\"x\":7,\"y\":7},{\"seat\":2,\"t\":\"turn\"}],\"ok\":true}");

    EXPECT_STATUS(vm, "snapshot", NULL, BSE_OK);
    char* second = snapshot_argument(g_out);
    CHECK(second != NULL);
    EXPECT_STATUS(vm, "restore", second, BSE_OK);
    EXPECT_STATUS(vm, "snapshot", NULL, BSE_OK);
    char* third = snapshot_argument(g_out);
    CHECK(third != NULL && second != NULL && strcmp(second, third) == 0);

    EXPECT_FAULT(vm, "restore", "{\"snapshot\":\"not json\"}", "BAD_SNAPSHOT", "not valid JSON");
    EXPECT_FAULT(vm, "restore", "{\"snapshot\":\"{\\\"v\\\":2}\"}", "BAD_SNAPSHOT", "unsupported snapshot version");
    EXPECT_FAULT(vm, "restore", "{\"snapshot\":\"{\\\"v\\\":1}\"}", "BAD_SNAPSHOT", "snapshot.view must be an array");
    EXPECT_FAULT(vm, "restore", "{\"snapshot\":5}", "BAD_ARGUMENT", "snapshot must be a string");
    EXPECT_FAULT(vm, "restore", "{}", "BAD_ARGUMENT", "snapshot must be a string");
    EXPECT_FAULT(vm, "restore", "{\"snapshot\":\"{\\\"v\\\":1,\\\"view\\\":[5]}\"}", "BAD_SNAPSHOT", "non-object");
    EXPECT_FAULT(vm, "restore", "{\"snapshot\":\"{\\\"v\\\":1,\\\"view\\\":[{\\\"id\\\":1,\\\"x\\\":1,\\\"y\\\":1}]}\"}", "BAD_SNAPSHOT", "view.kind");

    free(restore_args);
    free(second);
    free(third);
    bse_vm_destroy(vm);

    vm = open_vm("sim.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"amount\":10}}", BSE_OK);
    CHECK_STR(g_out, "{\"events\":[{\"t\":\"over\",\"winner\":1}],\"ok\":true}");
    bse_vm_destroy(vm);
}

static void test_view_api(void)
{
    SECTION("view");
    bse_vm* vm = open_vm("view.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"lifecycle\"}}",
        "{\"events\":[{\"id\":1,\"kind\":\"piece\",\"owner\":1,\"props\":{\"color\":\"red\"},\"t\":\"spawn\",\"x\":2,\"y\":3},"
        "{\"id\":1,\"t\":\"move\",\"x\":4,\"y\":5},{\"id\":1,\"props\":{\"color\":\"blue\",\"n\":2},\"t\":\"set\"},"
        "{\"level\":\"warn\",\"t\":\"status\",\"text\":\"4,5\"}],\"ok\":true}");
    EXPECT_CALL(vm, "query", "{\"name\":\"view\"}",
        "{\"ok\":true,\"value\":[{\"id\":1,\"kind\":\"piece\",\"owner\":1,\"props\":{\"color\":\"blue\",\"n\":2},\"x\":4,\"y\":5}]}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"get_copy\"}}",
        "{\"events\":[{\"level\":\"info\",\"t\":\"status\",\"text\":\"4,blue\"}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"remove\"}}", "{\"events\":[{\"id\":1,\"t\":\"remove\"}],\"ok\":true}");
    EXPECT_CALL(vm, "query", "{\"name\":\"view\"}", "{\"ok\":true,\"value\":[]}");

    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"spawn_bad\"}}", "BAD_ARGUMENT", "x must be a number");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"spawn_bad_kind\"}}", "BAD_ARGUMENT", "kind must be a string");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"remove_missing\"}}", "NO_SUCH_OBJECT", "999");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"move_missing\"}}", "NO_SUCH_OBJECT", "999");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"set_missing\"}}", "NO_SUCH_OBJECT", "999");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"emit\"}}",
        "{\"events\":[{\"name\":\"click\",\"t\":\"sound\",\"volume\":0.5}],\"ok\":true}");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"emit_reserved\"}}", "BAD_ARGUMENT", "reserved");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"emit_bad\"}}", "BAD_ARGUMENT", "string field 't'");

    EXPECT_CALL(vm, "command", "{\"seat\":2,\"cmd\":{\"action\":\"over\"}}",
        "{\"events\":[{\"cells\":[[1,1],[2,2]],\"reason\":\"five\",\"t\":\"over\",\"winner\":2}],\"ok\":true}");
    EXPECT_CALL(vm, "query", "{\"name\":\"session\"}", "{\"ok\":true,\"value\":{\"over\":{\"cells\":[[1,1],[2,2]],\"reason\":\"five\",\"winner\":2},\"turn\":1}}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"draw\"}}",
        "{\"events\":[{\"reason\":\"board full\",\"t\":\"over\",\"winner\":0}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"over_nil\"}}", "{\"events\":[{\"t\":\"over\",\"winner\":0}],\"ok\":true}");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"deepcopy\"}}",
        "{\"events\":[{\"level\":\"info\",\"t\":\"status\",\"text\":\"1/99\"}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"clear\"}}", "{\"events\":[{\"t\":\"reset\"}],\"ok\":true}");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"turn_nan\"}}", "BAD_ARGUMENT", "seat must be a number");
    EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"turn_str\"}}", "BAD_ARGUMENT", "seat must be a number");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"zzz\"}}",
        "{\"error\":{\"code\":\"UNKNOWN\",\"details\":{\"action\":\"zzz\"},\"message\":\"unknown action\"},\"ok\":false}");
    bse_vm_destroy(vm);
}

static void test_boot_failures(void)
{
    SECTION("boot failures");
    {
        bse_vm* vm = new_vm(NULL);
        add_file(vm, "main.luau", "syntax_error.luau");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_COMPILE);
        CHECK_HAS(last_result(vm), "main.luau:3:");
        CHECK_HAS(last_result(vm), "Expected");
        bse_stats stats = get_stats(vm);
        CHECK_INT(stats.faulted, 1);
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_FAULTED);
        CHECK_INT(bse_vm_call(vm, "ping", 4, NULL, 0, 0), BSE_ERR_FAULTED);
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_file(vm, "main.luau", "no_table.luau");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "must return a table");
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_file(vm, "main.luau", "runtime_error_boot.luau");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "field");
        CHECK_HAS(last_result(vm), "main.luau:2");
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_file(vm, "main.luau", "cycle_main.luau");
        add_file(vm, "lib/cycle_a.luau", "lib/cycle_a.luau");
        add_file(vm, "lib/cycle_b.luau", "lib/cycle_b.luau");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "cyclic require");
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_text(vm, "main.luau", "return { new_game = function() end }");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "rules.command must be a function");
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_text(vm, "main.luau", "return { new_game = function() end, command = 5 }");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "rules.command must be a function");
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_text(vm, "main.luau", "error({ code = 'X' })");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        bse_vm_destroy(vm);
    }
    {
        bse_vm* vm = new_vm(NULL);
        add_text(vm, "main.luau", "return require('./missing')");
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_RUNTIME);
        CHECK_HAS(last_result(vm), "was not found");
        bse_vm_destroy(vm);
    }
}

static void test_require(void)
{
    SECTION("require");
    bse_options options = make_options();
    bse_vm* vm = new_vm(&options);
    add_file(vm, "main.luau", "require_main.luau");
    add_file(vm, "lib/util.luau", "lib/util.luau");
    add_file(vm, "lib/other.luau", "lib/other.luau");
    add_file(vm, "lib/pkg/init.luau", "lib/pkg/init.luau");
    add_file(vm, "lib/pkg/sibling.luau", "lib/pkg/sibling.luau");
    CHECK_INT(boot_entry(vm, "main.luau"), BSE_OK);

    EXPECT_CALL(vm, "query", "{\"name\":\"answer\"}", "{\"ok\":true,\"value\":142}");
    EXPECT_CALL(vm, "query", "{\"name\":\"tail\"}", "{\"ok\":true,\"value\":42}");
    EXPECT_CALL(vm, "query", "{\"name\":\"cached\"}", "{\"ok\":true,\"value\":true}");
    EXPECT_FAULT(vm, "query", "{\"name\":\"absolute\"}", "BAD_REQUIRE", "must start with");
    EXPECT_FAULT(vm, "query", "{\"name\":\"escape\"}", "BAD_REQUIRE", "escapes the mod root");
    EXPECT_FAULT(vm, "query", "{\"name\":\"missing\"}", "BAD_REQUIRE", "was not found");
    bse_vm_destroy(vm);

    vm = new_vm(NULL);
    add_file(vm, "main.luau", "nil_module_main.luau");
    add_file(vm, "lib/nil_module.luau", "lib/nil_module.luau");
    CHECK_INT(boot_entry(vm, "main.luau"), BSE_OK);
    EXPECT_CALL(vm, "query", "{\"name\":\"x\"}", "{\"ok\":true,\"value\":{\"first\":true,\"second\":true}}");
    bse_vm_destroy(vm);

    vm = new_vm(NULL);
    add_text(vm, "main.luau", "local rules = require('./sub/rules') return rules");
    add_text(vm, "sub/rules.luau", "local helper = require('../shared') return { new_game = function() bse.turn(helper.first) end, command = function() end }");
    add_text(vm, "shared.luau", "return { first = 2 }");
    CHECK_INT(boot_entry(vm, "main.luau"), BSE_OK);
    EXPECT_CALL(vm, "new_game", NULL, "{\"events\":[{\"t\":\"reset\"},{\"seat\":2,\"t\":\"turn\"}],\"ok\":true}");
    bse_vm_destroy(vm);
}

static void test_sandbox(void)
{
    SECTION("sandbox");
    bse_vm* vm = open_vm("faulty.luau", NULL);
    EXPECT_CALL(vm, "query", "{\"name\":\"probe\"}",
        "{\"ok\":true,\"value\":{\"debug_info\":\"function\",\"dofile\":false,\"getfenv\":false,\"io\":false,\"loadstring\":false,\"os\":false,\"os_via_g\":false,"
        "\"rawset_g\":false,\"require\":\"function\",\"set_string_meta\":false,\"setfenv\":false,\"write_bse\":false,\"write_global\":false,\"write_math\":false,"
        "\"write_string\":false,\"write_view\":false}}");
    EXPECT_FAULT(vm, "eval", "{\"code\":\"1\"}", "DEV_ONLY", NULL);
    bse_vm_destroy(vm);

    bse_options options = make_options();
    options.flags = BSE_FLAG_DEV;
    vm = open_vm("basic.luau", &options);
    EXPECT_CALL(vm, "eval", "{\"code\":\"1 + 1\"}", "{\"ok\":true,\"values\":[\"2\"]}");
    EXPECT_CALL(vm, "eval", "{\"code\":\"1, 'a', nil, true\"}", "{\"ok\":true,\"values\":[\"1\",\"\\\"a\\\"\",\"null\",\"true\"]}");
    EXPECT_CALL(vm, "eval", "{\"code\":\"type(rules.new_game)\"}", "{\"ok\":true,\"values\":[\"\\\"function\\\"\"]}");
    EXPECT_CALL(vm, "eval", "{\"code\":\"local x = 5\"}", "{\"ok\":true,\"values\":[]}");
    EXPECT_CALL(vm, "eval", "{\"code\":\"type(print)\"}", "{\"ok\":true,\"values\":[\"\\\"function\\\"\"]}");
    EXPECT_FAULT(vm, "eval", "{\"code\":\"1 +\"}", "COMPILE_ERROR", "eval:");
    EXPECT_FAULT(vm, "eval", "{\"code\":\"error('x')\"}", "SCRIPT_ERROR", "x");
    EXPECT_FAULT(vm, "eval", "{\"code\":5}", "BAD_ARGUMENT", "code must be a string");
    EXPECT_CALL(vm, "eval", "{\"code\":\"loadstring == nil and os == nil and io == nil\"}", "{\"ok\":true,\"values\":[\"true\"]}");
    bse_vm_destroy(vm);
}

static void test_logs(void)
{
    SECTION("logs");
    bse_vm* vm = open_vm("logs.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    CHECK_STR(take_logs(vm), "[]");

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"levels\"}}", BSE_OK);
    CHECK_STR(take_logs(vm), "[{\"l\":0,\"m\":\"a\\t1\\tnil\\ttrue\"},{\"l\":1,\"m\":\"w\"},{\"l\":2,\"m\":\"e\"},{\"l\":0,\"m\":\"p\\t2.5\"}]");
    CHECK_STR(take_logs(vm), "[]");

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"values\"}}", BSE_OK);
    CHECK_STR(take_logs(vm), "[{\"l\":0,\"m\":\"nil\\tfalse\\t12\\ts\"}]");

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"badutf8\"}}", BSE_OK);
    CHECK_STR(take_logs(vm), "[{\"l\":0,\"m\":\"a\xEF\xBF\xBD" "b\"}]");

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"long\"}}", BSE_OK);
    {
        const char* logs = take_logs(vm);
        size_t expected = strlen("[{\"l\":0,\"m\":\"") + 4096 + 3 + strlen("\"}]");
        CHECK_INT(strlen(logs), expected);
        CHECK_HAS(logs, "xxx...\"}]");
    }

    EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"flood\"}}", BSE_OK);
    {
        bse_stats stats = get_stats(vm);
        CHECK_INT(stats.dropped_logs, 5000 - 2048);
        const char* logs = take_logs(vm);
        CHECK_INT(count_of(logs, "{\"l\":0,\"m\":\"x\"}"), 2048);
    }
    bse_vm_destroy(vm);
}

typedef struct
{
    const char* json;
    const char* expected;
} EchoCase;

static void test_json_roundtrip(void)
{
    SECTION("json roundtrip");
    bse_vm* vm = open_vm("basic.luau", NULL);

    static const EchoCase cases[] = {
        {"null", "{\"ok\":true}"},
        {"1", "{\"ok\":true,\"value\":1}"},
        {"-0", "{\"ok\":true,\"value\":0}"},
        {"-150", "{\"ok\":true,\"value\":-150}"},
        {"-1.5e2", "{\"ok\":true,\"value\":-150}"},
        {"1E5", "{\"ok\":true,\"value\":100000}"},
        {"1e+2", "{\"ok\":true,\"value\":100}"},
        {"1e21", "{\"ok\":true,\"value\":1e+21}"},
        {"1e-7", "{\"ok\":true,\"value\":1e-07}"},
        {"1e-400", "{\"ok\":true,\"value\":0}"},
        {"0.1", "{\"ok\":true,\"value\":0.1}"},
        {"0.30000000000000004", "{\"ok\":true,\"value\":0.30000000000000004}"},
        {"9007199254740993", "{\"ok\":true,\"value\":9007199254740992}"},
        {"123456789012345", "{\"ok\":true,\"value\":123456789012345}"},
        {"true", "{\"ok\":true,\"value\":true}"},
        {"false", "{\"ok\":true,\"value\":false}"},
        {"\"\"", "{\"ok\":true,\"value\":\"\"}"},
        {"\"plain\"", "{\"ok\":true,\"value\":\"plain\"}"},
        {"\"h\\u00e9llo \\u00E9 \\ud83d\\ude00\"", "{\"ok\":true,\"value\":\"h\xC3\xA9llo \xC3\xA9 \xF0\x9F\x98\x80\"}"},
        {"\"h\xC3\xA9llo \xF0\x9F\x98\x80\"", "{\"ok\":true,\"value\":\"h\xC3\xA9llo \xF0\x9F\x98\x80\"}"},
        {"\"line\\nbreak\\t\\\"q\\\" \\\\ \\/ \\b\\f\\r \\u0001 \\u001f\"", "{\"ok\":true,\"value\":\"line\\nbreak\\t\\\"q\\\" \\\\ / \\b\\f\\r \\u0001 \\u001f\"}"},
        {"\"a\\u0000b\"", "{\"ok\":true,\"value\":\"a\\u0000b\"}"},
        {"\"del\x7f\"", "{\"ok\":true,\"value\":\"del\x7f\"}"},
        {"[]", "{\"ok\":true,\"value\":[]}"},
        {"{}", "{\"ok\":true,\"value\":[]}"},
        {"[1,2,3]", "{\"ok\":true,\"value\":[1,2,3]}"},
        {"[true,false,null]", "{\"ok\":true,\"value\":[true,false,null]}"},
        {"[null,null]", "{\"ok\":true,\"value\":[null,null]}"},
        {"[[1],[2,[3]],[]]", "{\"ok\":true,\"value\":[[1],[2,[3]],[]]}"},
        {"{\"b\":1,\"a\":[1,2,{\"c\":null}]}", "{\"ok\":true,\"value\":{\"a\":[1,2,[]],\"b\":1}}"},
        {"{\"b\":1,\"a\":2,\"B\":3}", "{\"ok\":true,\"value\":{\"B\":3,\"a\":2,\"b\":1}}"},
        {"{\"a\":1,\"a\":2}", "{\"ok\":true,\"value\":{\"a\":2}}"},
        {"{\"\":1}", "{\"ok\":true,\"value\":{\"\":1}}"},
        {"{\"\\u00e9\":1}", "{\"ok\":true,\"value\":{\"\xC3\xA9\":1}}"},
        {" [ 1 , { \"a\" : \"b\" } ] ", "{\"ok\":true,\"value\":[1,{\"a\":\"b\"}]}"},
        {"[\n\t1,\r\n\t2\n]", "{\"ok\":true,\"value\":[1,2]}"},
    };
    for (size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i)
    {
        char args[512];
        snprintf(args, sizeof(args), "{\"name\":\"echo\",\"args\":%s}", cases[i].json);
        int status = call(vm, "query", args);
        ++g_checks;
        if (status != BSE_OK || strcmp(g_out, cases[i].expected) != 0)
        {
            fail_at(__FILE__, __LINE__);
            fprintf(stderr, "  input: %s\n  status: %d\n  expected: %s\n  actual:   %s\n", cases[i].json, status, cases[i].expected, g_out);
        }
    }

    {
        size_t count = 100000;
        size_t cap = count * 8 + 64;
        char* args = (char*)malloc(cap);
        size_t n = (size_t)snprintf(args, cap, "{\"name\":\"echo\",\"args\":[");
        for (size_t i = 1; i <= count; ++i)
            n += (size_t)snprintf(args + n, cap - n, "%s%zu", i > 1 ? "," : "", i);
        snprintf(args + n, cap - n, "]}");
        EXPECT_STATUS(vm, "query", args, BSE_OK);
        CHECK(strncmp(g_out, "{\"ok\":true,\"value\":[1,2,3,", 25) == 0);
        CHECK_HAS(g_out, ",99999,100000]}");
        free(args);
    }

    static const char* const malformed[] = {
        "{\"name\":\"echo\",\"args\":[1,2}",
        "{\"name\":\"echo\",\"args\":[1,]}",
        "{\"name\":\"echo\",\"args\":{\"a\":1,}}",
        "{\"name\":\"echo\",\"args\":01}",
        "{\"name\":\"echo\",\"args\":+1}",
        "{\"name\":\"echo\",\"args\":.5}",
        "{\"name\":\"echo\",\"args\":1.}",
        "{\"name\":\"echo\",\"args\":1e}",
        "{\"name\":\"echo\",\"args\":-}",
        "{\"name\":\"echo\",\"args\":NaN}",
        "{\"name\":\"echo\",\"args\":Infinity}",
        "{\"name\":\"echo\",\"args\":1e999}",
        "{\"name\":\"echo\",\"args\":\"\\ud800\"}",
        "{\"name\":\"echo\",\"args\":\"\\udc00\"}",
        "{\"name\":\"echo\",\"args\":\"\\ud800\\u0041\"}",
        "{\"name\":\"echo\",\"args\":\"\\ud800x\"}",
        "{\"name\":\"echo\",\"args\":\"\\u12\"}",
        "{\"name\":\"echo\",\"args\":\"\\uzzzz\"}",
        "{\"name\":\"echo\",\"args\":\"bad \\x\"}",
        "{\"name\":\"echo\",\"args\":\"\xff\"}",
        "{\"name\":\"echo\",\"args\":\"\xc0\xaf\"}",
        "{\"name\":\"echo\",\"args\":\"\xed\xa0\x80\"}",
        "{\"name\":\"echo\",\"args\":\"\xf4\x90\x80\x80\"}",
        "{\"name\":\"echo\",\"args\":\"\xe2\x82\"}",
        "{\"name\":\"echo\",\"args\":\"a\tb\"}",
        "{\"name\":\"echo\",\"args\":\"unterminated}",
        "{\"name\":\"echo\",\"args\":tru}",
        "{\"name\":\"echo\",\"args\":nul}",
        "{\"name\":\"echo\",\"args\":1} x",
        "{\"name\":\"echo\",\"args\":1}{}",
        "{'name':1}",
        "{\"name\" 1}",
        "{1:2}",
        "{\"name\":\"echo\"",
        "{",
        "[",
        "   ",
        "/* c */ {}",
    };
    for (size_t i = 0; i < sizeof(malformed) / sizeof(malformed[0]); ++i)
    {
        int status = call(vm, "query", malformed[i]);
        ++g_checks;
        if (status != BSE_ERR_JSON || strstr(g_out, "arguments: ") != g_out)
        {
            fail_at(__FILE__, __LINE__);
            fprintf(stderr, "  input: %s\n  status: %d\n  result: %.200s\n", malformed[i], status, g_out);
        }
    }

    {
        char deep[512];
        size_t k;
        for (k = 62; k <= 64; ++k)
        {
            size_t n = (size_t)snprintf(deep, sizeof(deep), "{\"name\":\"echo\",\"args\":");
            for (size_t i = 0; i < k; ++i)
                deep[n++] = '[';
            for (size_t i = 0; i < k; ++i)
                deep[n++] = ']';
            deep[n++] = '}';
            deep[n] = 0;
            int status = call(vm, "query", deep);
            CHECK_INT(status, k <= 63 ? BSE_OK : BSE_ERR_JSON);
            if (k == 64)
                CHECK_HAS(g_out, "deeper than 64");
        }
    }

    EXPECT_STATUS(vm, "query", "{\"name\":\"echo\",\"args\":[1]}", BSE_OK);
    CHECK_INT(get_stats(vm).faulted, 0);

    {
        size_t cap = (17u << 20) + 64;
        char* big = (char*)malloc(cap);
        memset(big, ' ', cap - 1);
        big[cap - 1] = 0;
        memcpy(big, "{}", 2);
        int32_t rc = bse_vm_call(vm, "ping", 4, big, (int32_t)(cap - 1), 0);
        CHECK_INT(rc, BSE_ERR_ARGUMENT);
        free(big);
    }
    bse_vm_destroy(vm);
}

static void test_json_encode_failures(void)
{
    SECTION("json encode failures");
    bse_vm* vm = open_vm("enc.luau", NULL);

    struct
    {
        const char* name;
        const char* fragment;
    } failures[] = {
        {"holes", "array has holes at $.value"},
        {"mixed", "mixes array and object keys at $.value"},
        {"nan", "non-finite number at $.value"},
        {"inf", "non-finite number at $.value"},
        {"cyclic", "deeper than 64 levels"},
        {"badutf8", "string is not valid UTF-8 at $.value"},
        {"badkey", "object key is not valid UTF-8"},
        {"negkey", "not a positive integer"},
        {"floatkey", "not a positive integer"},
        {"boolkey", "unsupported type 'boolean'"},
        {"nested", "type 'function' at $.value.a.b.c"},
        {"big", "too large"},
        {"thread", "type 'thread'"},
        {"buffer", "type 'buffer'"},
        {"vector", "type 'vector'"},
    };
    for (size_t i = 0; i < sizeof(failures) / sizeof(failures[0]); ++i)
    {
        char args[128];
        snprintf(args, sizeof(args), "{\"name\":\"%s\"}", failures[i].name);
        int status = call(vm, "query", args);
        ++g_checks;
        if (status != BSE_ERR_JSON || !strstr(g_out, failures[i].fragment) || strstr(g_out, "result: ") != g_out)
        {
            fail_at(__FILE__, __LINE__);
            fprintf(stderr, "  query: %s\n  status: %d\n  expected fragment: %s\n  actual: %.300s\n", failures[i].name, status, failures[i].fragment, g_out);
        }
    }

    EXPECT_CALL(vm, "query", "{\"name\":\"null\"}", "{\"ok\":true,\"value\":null}");
    EXPECT_CALL(vm, "query", "{\"name\":\"nulls\"}", "{\"ok\":true,\"value\":[null,1,null]}");
    EXPECT_CALL(vm, "query", "{\"name\":\"roundtrip\"}", "{\"ok\":true,\"value\":{\"a\":[1,2,{\"b\":true}],\"s\":\"x\"}}");
    EXPECT_FAULT(vm, "query", "{\"name\":\"decode_error\"}", "SCRIPT_ERROR", "json.decode: ");
    EXPECT_FAULT(vm, "query", "{\"name\":\"encode_error\"}", "SCRIPT_ERROR", "json.encode: ");
    EXPECT_CALL(vm, "query", "{\"name\":\"numbers\"}",
        "{\"ok\":true,\"value\":[0.5,-0.25,0.3333333333333333,9007199254740992,9.223372036854776e+18,1e+300,4.94065645841247e-324,123456789012,-7]}");
    CHECK_INT(get_stats(vm).faulted, 0);
    bse_vm_destroy(vm);
}

static void test_limits(void)
{
    SECTION("limits");

    const char* spinners[] = {"spin", "spin_pcall", "spin_recursive", "pattern", "gsub_spin"};
    for (size_t i = 0; i < sizeof(spinners) / sizeof(spinners[0]); ++i)
    {
        bse_options options = make_options();
        options.call_timeout_ms = 100;
        bse_vm* vm = open_vm("faulty.luau", &options);
        EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);

        char args[128];
        snprintf(args, sizeof(args), "{\"seat\":1,\"cmd\":{\"action\":\"%s\"}}", spinners[i]);
        double started = now_ms();
        int status = call(vm, "command", args);
        double elapsed = now_ms() - started;
        CHECK_INT(status, BSE_ERR_TIMEOUT);
        CHECK_HAS(g_out, "time limit of 100 ms");
        CHECK(elapsed >= 90.0);
        CHECK(elapsed < 3000.0);
        if (elapsed >= 3000.0)
            fprintf(stderr, "  %s took %.0f ms\n", spinners[i], elapsed);

        CHECK_INT(get_stats(vm).faulted, 1);
        CHECK_INT(call(vm, "ping", NULL), BSE_ERR_FAULTED);
        CHECK_INT(call(vm, "command", args), BSE_ERR_FAULTED);
        bse_vm_destroy(vm);
    }

    {
        bse_vm* vm = open_vm("faulty.luau", NULL);
        double started = now_ms();
        const char* spin_args = "{\"seat\":1,\"cmd\":{\"action\":\"spin\"}}";
        int32_t rc = bse_vm_call(vm, "command", 7, spin_args, (int32_t)strlen(spin_args), 40);
        double elapsed = now_ms() - started;
        CHECK_INT(rc, BSE_ERR_TIMEOUT);
        CHECK_HAS(last_result(vm), "time limit of 40 ms");
        CHECK(elapsed < 1500.0);
        bse_vm_destroy(vm);
    }

    {
        bse_options options = make_options();
        options.boot_timeout_ms = 100;
        bse_vm* vm = new_vm(&options);
        add_file(vm, "main.luau", "boot_spin.luau");
        double started = now_ms();
        CHECK_INT(boot_entry(vm, "main.luau"), BSE_ERR_TIMEOUT);
        CHECK(now_ms() - started < 3000.0);
        CHECK_INT(get_stats(vm).faulted, 1);
        bse_vm_destroy(vm);
    }

    {
        bse_options options = make_options();
        options.memory_limit_mb = 8;
        options.call_timeout_ms = 5000;
        const char* hogs[] = {"hog", "bigstring"};
        for (size_t i = 0; i < 2; ++i)
        {
            bse_vm* vm = open_vm("faulty.luau", &options);
            char args[128];
            snprintf(args, sizeof(args), "{\"seat\":1,\"cmd\":{\"action\":\"%s\"}}", hogs[i]);
            CHECK_INT(call(vm, "command", args), BSE_ERR_MEMORY);
            CHECK_HAS(g_out, "memory limit");
            bse_stats stats = get_stats(vm);
            CHECK_INT(stats.faulted, 1);
            CHECK(stats.memory_peak_bytes <= (8u << 20));
            CHECK_INT(call(vm, "ping", NULL), BSE_ERR_FAULTED);
            bse_vm_destroy(vm);
        }

        bse_vm* vm = open_vm("faulty.luau", &options);
        EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"alloc\"}}", BSE_OK);
        CHECK_INT(get_stats(vm).faulted, 0);
        bse_vm_destroy(vm);
    }

    {
        bse_vm* vm = open_vm("faulty.luau", NULL);
        EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"recurse\"}}", "SCRIPT_ERROR", "stack overflow");
        CHECK_INT(get_stats(vm).faulted, 0);
        EXPECT_STATUS(vm, "query", "{\"name\":\"probe\"}", BSE_OK);
        EXPECT_FAULT(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"yield\"}}", "SCRIPT_ERROR", "yield");
        EXPECT_STATUS(vm, "query", "{\"name\":\"probe\"}", BSE_OK);
        bse_vm_destroy(vm);
    }

    {
        bse_vm* a = open_vm("faulty.luau", NULL);
        bse_vm* b = open_vm("basic.luau", NULL);
        EXPECT_STATUS(b, "new_game", "{}", BSE_OK);
        const char* spin_args = "{\"seat\":1,\"cmd\":{\"action\":\"spin\"}}";
        int32_t rc = bse_vm_call(a, "command", 7, spin_args, (int32_t)strlen(spin_args), 30);
        CHECK_INT(rc, BSE_ERR_TIMEOUT);
        EXPECT_STATUS(b, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":1,\"y\":1}}", BSE_OK);
        CHECK_INT(get_stats(b).faulted, 0);
        bse_vm_destroy(a);
        bse_vm_destroy(b);
    }
}

static void test_capabilities(void)
{
    SECTION("capabilities");
    bse_vm* vm = open_vm("basic.luau", NULL);
    EXPECT_CALL(vm, "capabilities", NULL, "{\"legal\":false,\"ok\":true,\"query\":true,\"random\":false,\"snapshot\":true}");
    EXPECT_FAULT(vm, "random", "{\"seat\":1}", "NOT_SUPPORTED", "random needs");
    bse_vm_destroy(vm);

    vm = open_vm("sim.luau", NULL);
    EXPECT_CALL(vm, "capabilities", NULL, "{\"legal\":false,\"ok\":true,\"query\":false,\"random\":true,\"snapshot\":true}");
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    EXPECT_STATUS(vm, "random", "{\"seat\":1,\"seed\":5}", BSE_OK);
    char* first = copy_text(g_out, strlen(g_out));
    CHECK_HAS(first, "\"command\":{\"action\":\"add\",\"amount\":");
    EXPECT_STATUS(vm, "random", "{\"seat\":1,\"seed\":5}", BSE_OK);
    CHECK_STR(g_out, first);
    free(first);
    EXPECT_FAULT(vm, "random", "{}", "BAD_ARGUMENT", "seat must be a number");
    bse_vm_destroy(vm);

    vm = open_vm("legal.luau", NULL);
    EXPECT_CALL(vm, "capabilities", NULL, "{\"legal\":true,\"ok\":true,\"query\":true,\"random\":true,\"snapshot\":true}");
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    EXPECT_STATUS(vm, "random", "{\"seat\":1,\"seed\":9}", BSE_OK);
    CHECK_HAS(g_out, "\"command\":{\"take\":");
    EXPECT_CALL(vm, "random", "{\"seat\":2,\"seed\":9}", "{\"ok\":true}");
    bse_vm_destroy(vm);
}

static void test_simulation(void)
{
    SECTION("simulation");
    bse_vm* vm = open_vm("sim.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);

    EXPECT_STATUS(vm, "simulate", "{\"games\":1000,\"seed\":7}", BSE_OK);
    char* first = copy_text(g_out, strlen(g_out));
    CHECK_HAS(first, "\"aborted\":0");
    CHECK_HAS(first, "\"rejected\":0");
    CHECK_HAS(first, "\"draws\":0");
    CHECK_HAS(first, "\"games\":1000");
    CHECK_HAS(first, "\"wins\":{\"1\":");
    CHECK_HAS(first, "\"2\":");

    EXPECT_STATUS(vm, "simulate", "{\"games\":1000,\"seed\":7}", BSE_OK);
    CHECK_STR(g_out, first);
    EXPECT_STATUS(vm, "simulate", "{\"games\":1000,\"seed\":8}", BSE_OK);
    CHECK(strcmp(g_out, first) != 0);
    free(first);

    EXPECT_CALL(vm, "query", "{\"name\":\"session\"}", "{\"ok\":true,\"value\":{\"turn\":0}}");
    EXPECT_CALL(vm, "new_game", "{}", "{\"events\":[{\"t\":\"reset\"},{\"seat\":1,\"t\":\"turn\"}],\"ok\":true}");
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"amount\":3}}", "{\"events\":[{\"seat\":2,\"t\":\"turn\"}],\"ok\":true}");

    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"amount\":3}}",
        "{\"error\":{\"code\":\"NOT_YOUR_TURN\",\"message\":\"not your turn\"},\"ok\":false}");
    EXPECT_CALL(vm, "command", "{\"seat\":2,\"cmd\":{\"amount\":\"x\"}}",
        "{\"error\":{\"code\":\"BAD_AMOUNT\",\"message\":\"amount must be a number\"},\"ok\":false}");

    EXPECT_STATUS(vm, "simulate", "{\"games\":0}", BSE_OK);
    CHECK_HAS(g_out, "\"games\":1");
    EXPECT_STATUS(vm, "simulate", "{\"games\":5,\"seed\":1,\"max_moves\":2}", BSE_OK);
    CHECK_HAS(g_out, "\"aborted\":5");
    CHECK_HAS(g_out, "\"moves\":10");
    bse_vm_destroy(vm);

    vm = open_vm("basic.luau", NULL);
    EXPECT_FAULT(vm, "simulate", "{\"games\":1}", "NOT_SUPPORTED", "random_command or rules.legal_commands");
    EXPECT_FAULT(vm, "legal", "{\"seat\":1}", "NOT_SUPPORTED", "legal_commands");
    bse_vm_destroy(vm);

    vm = open_vm("legal.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    EXPECT_CALL(vm, "legal", "{\"seat\":1}", "{\"commands\":[{\"take\":1},{\"take\":2},{\"take\":3}],\"ok\":true}");
    EXPECT_CALL(vm, "legal", "{\"seat\":2}", "{\"commands\":[],\"ok\":true}");
    EXPECT_FAULT(vm, "legal", "{}", "BAD_ARGUMENT", "seat must be a number");
    EXPECT_STATUS(vm, "simulate", "{\"games\":500,\"seed\":3}", BSE_OK);
    CHECK_HAS(g_out, "\"aborted\":0");
    CHECK_HAS(g_out, "\"draws\":0");
    CHECK_HAS(g_out, "\"games\":500");
    EXPECT_STATUS(vm, "simulate", "{\"games\":50,\"seed\":3,\"options\":{\"pile\":1}}", BSE_OK);
    CHECK_STR(g_out, "{\"ok\":true,\"stats\":{\"aborted\":0,\"draws\":0,\"games\":50,\"moves\":50,\"rejected\":0,\"wins\":{\"1\":50}}}");
    EXPECT_CALL(vm, "query", "{\"name\":\"headless\"}", "{\"ok\":true,\"value\":false}");
    bse_vm_destroy(vm);

    vm = open_vm("headless_sim.luau", NULL);
    EXPECT_CALL(vm, "simulate", "{\"games\":10}", "{\"ok\":true,\"stats\":{\"aborted\":0,\"draws\":0,\"games\":10,\"moves\":10,\"rejected\":0,\"wins\":{\"1\":10}}}");
    EXPECT_CALL(vm, "simulate", "{\"games\":10,\"options\":{\"mode\":\"reject\"}}",
        "{\"ok\":true,\"stats\":{\"aborted\":10,\"draws\":0,\"games\":10,\"moves\":0,\"rejected\":10,\"wins\":[]}}");
    EXPECT_CALL(vm, "simulate", "{\"games\":10,\"options\":{\"mode\":\"none\"}}",
        "{\"ok\":true,\"stats\":{\"aborted\":10,\"draws\":0,\"games\":10,\"moves\":0,\"rejected\":0,\"wins\":[]}}");
    EXPECT_CALL(vm, "simulate", "{\"games\":10,\"options\":{\"mode\":\"draw\"}}",
        "{\"ok\":true,\"stats\":{\"aborted\":0,\"draws\":10,\"games\":10,\"moves\":10,\"rejected\":0,\"wins\":[]}}");
    EXPECT_CALL(vm, "simulate", "{\"games\":10,\"max_moves\":5,\"options\":{\"mode\":\"stall\"}}",
        "{\"ok\":true,\"stats\":{\"aborted\":10,\"draws\":0,\"games\":10,\"moves\":50,\"rejected\":0,\"wins\":[]}}");
    EXPECT_CALL(vm, "simulate", "{\"games\":10,\"options\":{\"mode\":\"true\"}}",
        "{\"ok\":true,\"stats\":{\"aborted\":0,\"draws\":0,\"games\":10,\"moves\":10,\"rejected\":0,\"wins\":{\"1\":10}}}");
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    EXPECT_CALL(vm, "command", "{\"seat\":1,\"cmd\":{\"yes\":true}}", "{\"events\":[{\"t\":\"over\",\"winner\":1}],\"ok\":true}");
    bse_vm_destroy(vm);

    {
        bse_options options = make_options();
        options.call_timeout_ms = 50;
        vm = open_vm("sim.luau", &options);
        EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
        int status = call(vm, "simulate", "{\"games\":1000000,\"seed\":1}");
        CHECK_INT(status, BSE_ERR_TIMEOUT);
        CHECK_INT(get_stats(vm).faulted, 1);
        bse_vm_destroy(vm);
    }
}

typedef struct
{
    int index;
    int failures;
    int commands;
} Worker;

static int worker_call(bse_vm* vm, const char* method, const char* args, char* out, size_t cap)
{
    int32_t n = 0;
    int status = bse_vm_call(vm, method, (int32_t)strlen(method), args, (int32_t)strlen(args), 0);
    const char* result = bse_vm_result(vm, &n);
    size_t copy = (size_t)n < cap - 1 ? (size_t)n : cap - 1;
    memcpy(out, result, copy);
    out[copy] = 0;
    return status;
}

static void worker_main(Worker* w)
{
    char out[4096];
    bse_vm* vm = bse_vm_create(NULL);
    if (!vm)
    {
        w->failures++;
        return;
    }
    size_t n = 0;
    char* source = slurp("basic.luau", &n);
    int ok = bse_vm_add_module(vm, "main.luau", 9, source, (int32_t)n) == BSE_OK && bse_vm_boot(vm, "main.luau", 9) == BSE_OK;
    free(source);
    if (!ok)
    {
        w->failures++;
        bse_vm_destroy(vm);
        return;
    }
    if (worker_call(vm, "new_game", "{}", out, sizeof(out)) != BSE_OK)
        w->failures++;
    for (int i = 0; i < 2000; ++i)
    {
        char args[128];
        int seat = 1 + (i % 2);
        snprintf(args, sizeof(args), "{\"seat\":%d,\"cmd\":{\"action\":\"place\",\"x\":%d,\"y\":%d}}", seat, i % 15 + 1, i / 15 % 15 + 1);
        if (worker_call(vm, "command", args, out, sizeof(out)) != BSE_OK || !strstr(out, "\"ok\":true"))
            w->failures++;
        w->commands++;
    }
    if (worker_call(vm, "query", "{\"name\":\"counter\"}", out, sizeof(out)) != BSE_OK || strcmp(out, "{\"ok\":true,\"value\":2000}") != 0)
        w->failures++;
    bse_vm_destroy(vm);
}

#if defined(_WIN32)
static DWORD WINAPI worker_entry(LPVOID p)
{
    worker_main((Worker*)p);
    return 0;
}
#else
static void* worker_entry(void* p)
{
    worker_main((Worker*)p);
    return NULL;
}
#endif

static void test_threads(void)
{
    SECTION("threads");
    enum { kWorkers = 4 };
    Worker workers[kWorkers];
    memset(workers, 0, sizeof(workers));
#if defined(_WIN32)
    HANDLE handles[kWorkers];
    for (int i = 0; i < kWorkers; ++i)
    {
        workers[i].index = i;
        handles[i] = CreateThread(NULL, 0, worker_entry, &workers[i], 0, NULL);
    }
    for (int i = 0; i < kWorkers; ++i)
    {
        WaitForSingleObject(handles[i], INFINITE);
        CloseHandle(handles[i]);
    }
#else
    pthread_t threads[kWorkers];
    for (int i = 0; i < kWorkers; ++i)
    {
        workers[i].index = i;
        pthread_create(&threads[i], NULL, worker_entry, &workers[i]);
    }
    for (int i = 0; i < kWorkers; ++i)
        pthread_join(threads[i], NULL);
#endif
    for (int i = 0; i < kWorkers; ++i)
    {
        CHECK_INT(workers[i].failures, 0);
        CHECK_INT(workers[i].commands, 2000);
    }
}

static void test_lifetime(void)
{
    SECTION("lifetime");
    for (int i = 0; i < 200; ++i)
    {
        bse_options options = make_options();
        options.memory_limit_mb = 16;
        bse_vm* vm = open_vm("basic.luau", &options);
        EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
        EXPECT_STATUS(vm, "command", "{\"seat\":1,\"cmd\":{\"action\":\"place\",\"x\":1,\"y\":1}}", BSE_OK);
        bse_vm_destroy(vm);
    }
    bse_vm* vm = new_vm(NULL);
    bse_vm_destroy(vm);

    vm = new_vm(NULL);
    add_file(vm, "main.luau", "basic.luau");
    bse_vm_destroy(vm);
}

static void benchmark(void)
{
    SECTION("benchmark");
    bse_vm* vm = open_vm("sim.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    double started = now_ms();
    const char* sim_args = "{\"games\":50000,\"seed\":1}";
    int rc = bse_vm_call(vm, "simulate", 8, sim_args, (int32_t)strlen(sim_args), 120000);
    double elapsed = now_ms() - started;
    CHECK_INT(rc, BSE_OK);
    printf("benchmark: sim.luau 50000 games in %.0f ms (%.0f games/s)\n", elapsed, 50000.0 / (elapsed / 1000.0));
    bse_vm_destroy(vm);

    vm = open_vm("basic.luau", NULL);
    EXPECT_STATUS(vm, "new_game", "{}", BSE_OK);
    started = now_ms();
    const int calls = 20000;
    for (int i = 0; i < calls; ++i)
    {
        char args[128];
        snprintf(args, sizeof(args), "{\"seat\":1,\"cmd\":{\"action\":\"fail\"}}");
        if (bse_vm_call(vm, "command", 7, args, (int32_t)strlen(args), 0) != BSE_OK)
        {
            CHECK(0);
            break;
        }
    }
    elapsed = now_ms() - started;
    printf("benchmark: %d host calls in %.0f ms (%.1f us/call)\n", calls, elapsed, elapsed * 1000.0 / calls);
    bse_vm_destroy(vm);
}

int main(int argc, char** argv)
{
#if !defined(_WIN32)
    alarm(120);
#endif
    if (argc > 1)
        g_dir = argv[1];

    test_abi();
    test_lifecycle();
    test_snapshot();
    test_view_api();
    test_boot_failures();
    test_require();
    test_sandbox();
    test_logs();
    test_json_roundtrip();
    test_json_encode_failures();
    test_limits();
    test_capabilities();
    test_simulation();
    test_threads();
    test_lifetime();
    benchmark();

    free(g_out);
    printf("%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
