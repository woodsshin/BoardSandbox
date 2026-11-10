#pragma once

#include <cstddef>
#include <string>

struct lua_State;

namespace bse
{

constexpr int kJsonMaxDepth = 64;

extern char json_null_tag;

// Serializes the value at `idx` as canonical JSON (object keys sorted bytewise).
// May raise Lua errors on allocation failure; call from a protected context.
bool json_encode(lua_State* L, int idx, std::string& out, std::string& err, size_t max_bytes);

// Parses JSON text and pushes one value. JSON null becomes nil for object members,
// the json_null_tag sentinel for array elements, and nil at the top level.
bool json_decode(lua_State* L, const char* text, size_t length, std::string& err);

// Appends `s` as a quoted JSON string. Returns false when `s` is not valid UTF-8.
bool json_append_string(std::string& out, const char* s, size_t n);

// Appends `s` as a quoted JSON string, replacing invalid UTF-8 with U+FFFD.
void json_append_string_lossy(std::string& out, const char* s, size_t n);

} // namespace bse
