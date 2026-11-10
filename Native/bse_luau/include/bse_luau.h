#ifndef BSE_LUAU_H
#define BSE_LUAU_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#if defined(BSE_BUILDING)
#define BSE_API __declspec(dllexport)
#else
#define BSE_API __declspec(dllimport)
#endif
#else
#define BSE_API __attribute__((visibility("default")))
#endif

#define BSE_ABI_VERSION 1u

typedef struct bse_vm bse_vm;

typedef enum bse_status
{
    BSE_OK = 0,
    BSE_ERR_ARGUMENT = 1,
    BSE_ERR_COMPILE = 2,
    BSE_ERR_RUNTIME = 3,
    BSE_ERR_TIMEOUT = 4,
    BSE_ERR_MEMORY = 5,
    BSE_ERR_JSON = 6,
    BSE_ERR_FAULTED = 7,
    BSE_ERR_INTERNAL = 8,
    BSE_ERR_STATE = 9
} bse_status;

enum
{
    BSE_FLAG_DEV = 1u
};

typedef struct bse_options
{
    uint32_t struct_size;
    uint32_t flags;
    uint32_t memory_limit_mb;
    uint32_t call_timeout_ms;
    uint32_t boot_timeout_ms;
    int32_t optimization_level;
    int32_t debug_level;
} bse_options;

typedef struct bse_stats
{
    uint32_t struct_size;
    uint32_t faulted;
    uint64_t memory_bytes;
    uint64_t memory_peak_bytes;
    uint64_t call_count;
    uint64_t last_call_micros;
    uint64_t dropped_logs;
} bse_stats;

BSE_API uint32_t bse_abi_version(void);
BSE_API const char* bse_luau_version(void);

BSE_API bse_vm* bse_vm_create(const bse_options* options);
BSE_API void bse_vm_destroy(bse_vm* vm);

BSE_API int32_t bse_vm_add_module(bse_vm* vm, const char* path, int32_t path_len, const char* source, int32_t source_len);
BSE_API int32_t bse_vm_boot(bse_vm* vm, const char* entry, int32_t entry_len);
BSE_API int32_t bse_vm_call(bse_vm* vm, const char* method, int32_t method_len, const char* args_json, int32_t args_len, uint32_t timeout_ms);

BSE_API const char* bse_vm_result(bse_vm* vm, int32_t* out_len);
BSE_API const char* bse_vm_take_logs(bse_vm* vm, int32_t* out_len);
BSE_API int32_t bse_vm_get_stats(bse_vm* vm, bse_stats* out);

#ifdef __cplusplus
}
#endif

#endif
