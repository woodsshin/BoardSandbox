#include "json.h"

#include "lua.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>

namespace bse
{

char json_null_tag = 0;

namespace
{

size_t utf8_sequence_length(const unsigned char* s, const unsigned char* end)
{
    unsigned char c = s[0];
    if (c < 0x80)
        return 1;
    if (c >= 0xC2 && c <= 0xDF)
    {
        if (end - s < 2 || (s[1] & 0xC0) != 0x80)
            return 0;
        return 2;
    }
    if (c >= 0xE0 && c <= 0xEF)
    {
        if (end - s < 3 || (s[1] & 0xC0) != 0x80 || (s[2] & 0xC0) != 0x80)
            return 0;
        if (c == 0xE0 && s[1] < 0xA0)
            return 0;
        if (c == 0xED && s[1] >= 0xA0)
            return 0;
        return 3;
    }
    if (c >= 0xF0 && c <= 0xF4)
    {
        if (end - s < 4 || (s[1] & 0xC0) != 0x80 || (s[2] & 0xC0) != 0x80 || (s[3] & 0xC0) != 0x80)
            return 0;
        if (c == 0xF0 && s[1] < 0x90)
            return 0;
        if (c == 0xF4 && s[1] >= 0x90)
            return 0;
        return 4;
    }
    return 0;
}

void append_escaped_codepoint(std::string& out, unsigned char c)
{
    static const char hex[] = "0123456789abcdef";
    out += "\\u00";
    out.push_back(hex[(c >> 4) & 0xF]);
    out.push_back(hex[c & 0xF]);
}

// Shared by strict and lossy appenders; `lossy` substitutes U+FFFD for invalid bytes.
bool append_string_impl(std::string& out, const char* s, size_t n, bool lossy)
{
    const unsigned char* p = reinterpret_cast<const unsigned char*>(s);
    const unsigned char* end = p + n;
    size_t start = out.size();
    out.push_back('"');
    while (p < end)
    {
        unsigned char c = *p;
        if (c < 0x80)
        {
            switch (c)
            {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20)
                    append_escaped_codepoint(out, c);
                else
                    out.push_back(static_cast<char>(c));
            }
            ++p;
            continue;
        }
        size_t len = utf8_sequence_length(p, end);
        if (len == 0)
        {
            if (!lossy)
            {
                out.resize(start);
                return false;
            }
            out += "\xEF\xBF\xBD";
            ++p;
            continue;
        }
        out.append(reinterpret_cast<const char*>(p), len);
        p += len;
    }
    out.push_back('"');
    return true;
}

struct StackGuard
{
    lua_State* L;
    int top;
    explicit StackGuard(lua_State* state) : L(state), top(lua_gettop(state)) {}
    ~StackGuard() { lua_settop(L, top); }
};

struct PathSegment
{
    bool is_index;
    int index;
    std::string key;
};

class Encoder
{
public:
    Encoder(lua_State* state, std::string& output, std::string& error, size_t max_bytes)
        : L(state), out(output), err(error), limit(max_bytes)
    {
    }

    bool value(int idx, int depth)
    {
        if (depth > kJsonMaxDepth)
            return fail("nesting is deeper than 64 levels (cyclic table?)");
        idx = lua_absindex(L, idx);
        switch (lua_type(L, idx))
        {
        case LUA_TNIL:
            out += "null";
            return true;
        case LUA_TBOOLEAN:
            out += lua_toboolean(L, idx) ? "true" : "false";
            return true;
        case LUA_TNUMBER:
            return number(lua_tonumber(L, idx));
        case LUA_TINTEGER:
        {
            int isint = 0;
            int64_t v = lua_tointeger64(L, idx, &isint);
            out += std::to_string(static_cast<long long>(v));
            return true;
        }
        case LUA_TSTRING:
        {
            size_t n = 0;
            const char* s = lua_tolstring(L, idx, &n);
            if (!append_string_impl(out, s, n, false))
                return fail("string is not valid UTF-8");
            return true;
        }
        case LUA_TLIGHTUSERDATA:
            if (lua_tolightuserdata(L, idx) == &json_null_tag)
            {
                out += "null";
                return true;
            }
            return fail("cannot encode lightuserdata");
        case LUA_TTABLE:
            return table(idx, depth);
        default:
            return fail(std::string("cannot encode a value of type '") + lua_typename(L, lua_type(L, idx)) + "'");
        }
    }

private:
    lua_State* L;
    std::string& out;
    std::string& err;
    size_t limit;
    std::vector<PathSegment> path;

    bool fail(const std::string& message)
    {
        err = message;
        if (!path.empty())
        {
            err += " at $";
            for (const PathSegment& seg : path)
            {
                if (seg.is_index)
                    err += "[" + std::to_string(seg.index) + "]";
                else
                    err += "." + seg.key;
            }
        }
        return false;
    }

    bool number(double d)
    {
        if (!std::isfinite(d))
            return fail("cannot encode a non-finite number");
        if (d == std::floor(d) && std::fabs(d) < 9007199254740992.0)
        {
            out += std::to_string(static_cast<long long>(d));
            return true;
        }
        char buf[48];
        for (int precision = 15; precision <= 17; ++precision)
        {
            std::snprintf(buf, sizeof(buf), "%.*g", precision, d);
            if (std::strtod(buf, nullptr) == d)
                break;
        }
        for (char* c = buf; *c; ++c)
            if (*c == ',')
                *c = '.';
        out += buf;
        return true;
    }

    bool table(int idx, int depth)
    {
        StackGuard guard(L);
        if (!lua_checkstack(L, 6))
            return fail("stack overflow");

        size_t string_keys = 0;
        size_t number_keys = 0;
        double max_index = 0;
        lua_pushnil(L);
        while (lua_next(L, idx) != 0)
        {
            int key_type = lua_type(L, -2);
            if (key_type == LUA_TSTRING)
            {
                ++string_keys;
            }
            else if (key_type == LUA_TNUMBER)
            {
                double k = lua_tonumber(L, -2);
                if (k >= 1 && k == std::floor(k) && k <= 2147483647.0)
                {
                    ++number_keys;
                    max_index = std::max(max_index, k);
                }
                else
                {
                    return fail("table has a number key that is not a positive integer");
                }
            }
            else
            {
                return fail(std::string("table has a key of unsupported type '") + lua_typename(L, key_type) + "'");
            }
            lua_pop(L, 1);
        }

        if (string_keys > 0 && number_keys > 0)
            return fail("table mixes array and object keys");

        if (number_keys > 0)
        {
            if (max_index != static_cast<double>(number_keys))
                return fail("array has holes");
            return array(idx, static_cast<int>(number_keys), depth);
        }
        if (string_keys > 0)
            return object(idx, string_keys, depth);

        out += "[]";
        return true;
    }

    bool array(int idx, int count, int depth)
    {
        out.push_back('[');
        path.push_back(PathSegment{true, 0, std::string()});
        for (int i = 1; i <= count; ++i)
        {
            if (i > 1)
                out.push_back(',');
            path.back().index = i;
            lua_rawgeti(L, idx, i);
            bool ok = value(-1, depth + 1);
            lua_pop(L, 1);
            if (!ok)
                return false;
            if (out.size() > limit)
                return fail("encoded result is too large");
        }
        path.pop_back();
        out.push_back(']');
        return true;
    }

    bool object(int idx, size_t count, int depth)
    {
        std::vector<std::string> keys;
        keys.reserve(count);
        lua_pushnil(L);
        while (lua_next(L, idx) != 0)
        {
            size_t n = 0;
            const char* k = lua_tolstring(L, -2, &n);
            keys.emplace_back(k, n);
            lua_pop(L, 1);
        }
        std::sort(keys.begin(), keys.end());

        out.push_back('{');
        path.push_back(PathSegment{false, 0, std::string()});
        bool first = true;
        for (const std::string& key : keys)
        {
            if (!first)
                out.push_back(',');
            first = false;
            path.back().key = key;
            if (!append_string_impl(out, key.data(), key.size(), false))
                return fail("object key is not valid UTF-8");
            out.push_back(':');
            lua_pushlstring(L, key.data(), key.size());
            lua_rawget(L, idx);
            bool ok = value(-1, depth + 1);
            lua_pop(L, 1);
            if (!ok)
                return false;
            if (out.size() > limit)
                return fail("encoded result is too large");
        }
        path.pop_back();
        out.push_back('}');
        return true;
    }
};

class Decoder
{
public:
    Decoder(lua_State* state, const char* text, size_t length, std::string& error)
        : L(state), p(text), begin(text), end(text + length), err(error)
    {
    }

    bool run()
    {
        skip_whitespace();
        if (p == end)
            return fail("empty input");
        bool is_null = false;
        if (!value(0, is_null))
            return false;
        if (is_null)
            lua_pushnil(L);
        skip_whitespace();
        if (p != end)
            return fail("unexpected trailing characters");
        return true;
    }

private:
    lua_State* L;
    const char* p;
    const char* begin;
    const char* end;
    std::string& err;

    bool fail(const std::string& message)
    {
        err = message + " at offset " + std::to_string(static_cast<long long>(p - begin));
        return false;
    }

    void skip_whitespace()
    {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\n' || *p == '\r'))
            ++p;
    }

    bool literal(const char* word)
    {
        size_t n = std::strlen(word);
        if (static_cast<size_t>(end - p) < n || std::memcmp(p, word, n) != 0)
            return fail("invalid literal");
        p += n;
        return true;
    }

    // Pushes exactly one value, or nothing when the value is null (is_null = true).
    bool value(int depth, bool& is_null)
    {
        if (p >= end)
            return fail("unexpected end of input");
        if (!lua_checkstack(L, 8))
            return fail("stack overflow");
        switch (*p)
        {
        case '{':
            return object(depth + 1);
        case '[':
            return array(depth + 1);
        case '"':
        {
            std::string s;
            if (!string_token(s))
                return false;
            lua_pushlstring(L, s.data(), s.size());
            return true;
        }
        case 't':
            if (!literal("true"))
                return false;
            lua_pushboolean(L, 1);
            return true;
        case 'f':
            if (!literal("false"))
                return false;
            lua_pushboolean(L, 0);
            return true;
        case 'n':
            if (!literal("null"))
                return false;
            is_null = true;
            return true;
        default:
            return number();
        }
    }

    bool number()
    {
        const char* start = p;
        if (p < end && *p == '-')
            ++p;
        if (p >= end)
            return fail("invalid number");
        if (*p == '0')
        {
            ++p;
        }
        else if (*p >= '1' && *p <= '9')
        {
            while (p < end && *p >= '0' && *p <= '9')
                ++p;
        }
        else
        {
            p = start;
            return fail("unexpected character");
        }
        if (p < end && *p == '.')
        {
            ++p;
            if (p >= end || *p < '0' || *p > '9')
                return fail("invalid number");
            while (p < end && *p >= '0' && *p <= '9')
                ++p;
        }
        if (p < end && (*p == 'e' || *p == 'E'))
        {
            ++p;
            if (p < end && (*p == '+' || *p == '-'))
                ++p;
            if (p >= end || *p < '0' || *p > '9')
                return fail("invalid number");
            while (p < end && *p >= '0' && *p <= '9')
                ++p;
        }
        size_t n = static_cast<size_t>(p - start);
        if (n >= 64)
            return fail("number literal is too long");
        char buf[64];
        std::memcpy(buf, start, n);
        buf[n] = 0;
        double d = std::strtod(buf, nullptr);
        if (!std::isfinite(d))
            return fail("number is out of range");
        lua_pushnumber(L, d);
        return true;
    }

    static int hex_value(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        return -1;
    }

    bool read_hex4(unsigned& out_code)
    {
        if (end - p < 4)
            return fail("truncated unicode escape");
        unsigned v = 0;
        for (int i = 0; i < 4; ++i)
        {
            int h = hex_value(p[i]);
            if (h < 0)
                return fail("invalid unicode escape");
            v = (v << 4) | static_cast<unsigned>(h);
        }
        p += 4;
        out_code = v;
        return true;
    }

    static void append_utf8(std::string& s, unsigned cp)
    {
        if (cp < 0x80)
        {
            s.push_back(static_cast<char>(cp));
        }
        else if (cp < 0x800)
        {
            s.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
        else if (cp < 0x10000)
        {
            s.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
        else
        {
            s.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            s.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }

    bool string_token(std::string& s)
    {
        ++p;
        while (true)
        {
            if (p >= end)
                return fail("unterminated string");
            unsigned char c = static_cast<unsigned char>(*p);
            if (c == '"')
            {
                ++p;
                return true;
            }
            if (c < 0x20)
                return fail("control character in string");
            if (c == '\\')
            {
                ++p;
                if (p >= end)
                    return fail("unterminated escape");
                char e = *p++;
                switch (e)
                {
                case '"': s.push_back('"'); break;
                case '\\': s.push_back('\\'); break;
                case '/': s.push_back('/'); break;
                case 'b': s.push_back('\b'); break;
                case 'f': s.push_back('\f'); break;
                case 'n': s.push_back('\n'); break;
                case 'r': s.push_back('\r'); break;
                case 't': s.push_back('\t'); break;
                case 'u':
                {
                    unsigned cp = 0;
                    if (!read_hex4(cp))
                        return false;
                    if (cp >= 0xD800 && cp <= 0xDBFF)
                    {
                        if (end - p < 2 || p[0] != '\\' || p[1] != 'u')
                            return fail("unpaired surrogate");
                        p += 2;
                        unsigned low = 0;
                        if (!read_hex4(low))
                            return false;
                        if (low < 0xDC00 || low > 0xDFFF)
                            return fail("unpaired surrogate");
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    }
                    else if (cp >= 0xDC00 && cp <= 0xDFFF)
                    {
                        return fail("unpaired surrogate");
                    }
                    append_utf8(s, cp);
                    break;
                }
                default:
                    --p;
                    return fail("invalid escape");
                }
                continue;
            }
            if (c < 0x80)
            {
                s.push_back(static_cast<char>(c));
                ++p;
                continue;
            }
            size_t len = utf8_sequence_length(reinterpret_cast<const unsigned char*>(p), reinterpret_cast<const unsigned char*>(end));
            if (len == 0)
                return fail("invalid UTF-8");
            s.append(p, len);
            p += len;
        }
    }

    bool array(int depth)
    {
        if (depth > kJsonMaxDepth)
            return fail("nesting is deeper than 64 levels");
        ++p;
        lua_createtable(L, 0, 0);
        skip_whitespace();
        if (p < end && *p == ']')
        {
            ++p;
            return true;
        }
        int i = 1;
        while (true)
        {
            skip_whitespace();
            bool is_null = false;
            if (!value(depth, is_null))
                return false;
            if (is_null)
                lua_pushlightuserdata(L, &json_null_tag);
            lua_rawseti(L, -2, i++);
            skip_whitespace();
            if (p >= end)
                return fail("unterminated array");
            if (*p == ',')
            {
                ++p;
                continue;
            }
            if (*p == ']')
            {
                ++p;
                return true;
            }
            return fail("expected ',' or ']'");
        }
    }

    bool object(int depth)
    {
        if (depth > kJsonMaxDepth)
            return fail("nesting is deeper than 64 levels");
        ++p;
        lua_createtable(L, 0, 0);
        skip_whitespace();
        if (p < end && *p == '}')
        {
            ++p;
            return true;
        }
        while (true)
        {
            skip_whitespace();
            if (p >= end || *p != '"')
                return fail("expected a string key");
            std::string key;
            if (!string_token(key))
                return false;
            skip_whitespace();
            if (p >= end || *p != ':')
                return fail("expected ':'");
            ++p;
            skip_whitespace();
            lua_pushlstring(L, key.data(), key.size());
            bool is_null = false;
            if (!value(depth, is_null))
                return false;
            if (is_null)
                lua_pushnil(L);
            lua_rawset(L, -3);
            skip_whitespace();
            if (p >= end)
                return fail("unterminated object");
            if (*p == ',')
            {
                ++p;
                continue;
            }
            if (*p == '}')
            {
                ++p;
                return true;
            }
            return fail("expected ',' or '}'");
        }
    }
};

} // namespace

bool json_encode(lua_State* L, int idx, std::string& out, std::string& err, size_t max_bytes)
{
    Encoder encoder(L, out, err, max_bytes);
    return encoder.value(idx, 0);
}

bool json_decode(lua_State* L, const char* text, size_t length, std::string& err)
{
    StackGuard guard(L);
    int top = guard.top;
    Decoder decoder(L, text, length, err);
    if (!decoder.run())
        return false;
    // keep the decoded value: StackGuard would pop it, so move it down and detach
    lua_settop(L, top + 1);
    guard.top = top + 1;
    return true;
}

bool json_append_string(std::string& out, const char* s, size_t n)
{
    return append_string_impl(out, s, n, false);
}

void json_append_string_lossy(std::string& out, const char* s, size_t n)
{
    append_string_impl(out, s, n, true);
}

} // namespace bse
