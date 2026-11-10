#include "Luau/BuiltinDefinitions.h"
#include "Luau/Config.h"
#include "Luau/FileUtils.h"
#include "Luau/Flags.h"
#include "Luau/Frontend.h"
#include "Luau/ModuleResolver.h"
#include "Luau/StringUtils.h"

#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{

std::vector<std::string> split_path(const std::string& path)
{
    std::vector<std::string> parts;
    size_t start = 0;
    while (start <= path.size())
    {
        size_t end = path.find('/', start);
        if (end == std::string::npos)
            end = path.size();
        if (end > start)
            parts.push_back(path.substr(start, end - start));
        start = end + 1;
    }
    return parts;
}

std::string join_path(const std::vector<std::string>& parts, bool absolute)
{
    std::string out = absolute ? "/" : "";
    for (size_t i = 0; i < parts.size(); ++i)
    {
        if (i > 0)
            out += "/";
        out += parts[i];
    }
    return out;
}

struct Resolver : Luau::FileResolver
{
    std::optional<Luau::SourceCode> readSource(const Luau::ModuleName& name) override
    {
        std::optional<std::string> source = readFile(name);
        if (!source)
            return std::nullopt;
        return Luau::SourceCode{*source, Luau::SourceCode::Module};
    }

    std::optional<Luau::ModuleInfo> resolveModule(const Luau::ModuleInfo* context, Luau::AstExpr* node, const Luau::TypeCheckLimits&) override
    {
        const Luau::AstExprConstantString* expr = node->as<Luau::AstExprConstantString>();
        if (!expr)
            return std::nullopt;
        std::string spec(expr->value.data, expr->value.size);
        if (spec.rfind("./", 0) != 0 && spec.rfind("../", 0) != 0)
            return std::nullopt;

        bool absolute = !context->name.empty() && context->name[0] == '/';
        std::vector<std::string> parts = split_path(context->name);
        if (!parts.empty())
            parts.pop_back();
        for (const std::string& segment : split_path(spec))
        {
            if (segment == "..")
            {
                if (parts.empty())
                    return std::nullopt;
                parts.pop_back();
            }
            else if (segment != ".")
            {
                parts.push_back(segment);
            }
        }
        std::string base = join_path(parts, absolute);
        for (const std::string& candidate : {base + ".luau", base + "/init.luau"})
        {
            if (isFile(candidate))
                return Luau::ModuleInfo{candidate};
        }
        return std::nullopt;
    }

    std::string getHumanReadableModuleName(const Luau::ModuleName& name) const override
    {
        return name;
    }
};

struct ConfigResolver : Luau::ConfigResolver
{
    Luau::Config config;

    explicit ConfigResolver(Luau::Mode mode) { config.mode = mode; }

    const Luau::Config& getConfig(const Luau::ModuleName&, const Luau::TypeCheckLimits&) const override { return config; }
};

void report(const char* name, const Luau::Location& loc, const char* kind, const std::string& message)
{
    std::fprintf(stderr, "%s(%d,%d): %s: %s\n", name, loc.begin.line + 1, loc.begin.column + 1, kind, message.c_str());
}

} // namespace

int main(int argc, char** argv)
{
    setLuauFlagsDefault();

    Luau::Mode mode = Luau::Mode::Nonstrict;
    std::vector<std::string> definitions;
    std::vector<std::string> files;
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--strict") == 0)
            mode = Luau::Mode::Strict;
        else if (std::strncmp(argv[i], "--defs=", 7) == 0)
            definitions.emplace_back(argv[i] + 7);
        else if (argv[i][0] == '-')
        {
            std::fprintf(stderr, "usage: %s [--strict] [--defs=file.d.luau]... files...\n", argv[0]);
            return 1;
        }
        else
            files.emplace_back(argv[i]);
    }

    Resolver resolver;
    ConfigResolver configs(mode);
    Luau::FrontendOptions options;
    options.runLintChecks = true;
    Luau::Frontend frontend(Luau::SolverMode::New, &resolver, &configs, options);
    Luau::registerBuiltinGlobals(frontend, frontend.globals);

    for (const std::string& path : definitions)
    {
        std::optional<std::string> source = readFile(path);
        if (!source)
        {
            std::fprintf(stderr, "cannot read definitions: %s\n", path.c_str());
            return 2;
        }
        Luau::LoadDefinitionFileResult result = frontend.loadDefinitionFile(frontend.globals, frontend.globals.globalScope, *source, path, false, false);
        if (!result.success)
        {
            for (const Luau::ParseError& e : result.parseResult.errors)
                report(path.c_str(), e.getLocation(), "SyntaxError", e.getMessage());
            if (result.module)
                for (const Luau::TypeError& e : result.module->errors)
                    report(path.c_str(), e.location, "TypeError", Luau::toString(e));
            return 2;
        }
    }
    Luau::freeze(frontend.globals.globalTypes);

    int failed = 0;
    for (const std::string& path : files)
    {
        Luau::CheckResult result = frontend.check(path);
        if (!frontend.getSourceModule(path))
        {
            std::fprintf(stderr, "cannot read %s\n", path.c_str());
            ++failed;
            continue;
        }
        for (const Luau::TypeError& e : result.errors)
        {
            std::string name = frontend.fileResolver->getHumanReadableModuleName(e.moduleName);
            if (const Luau::SyntaxError* syntax = Luau::get_if<Luau::SyntaxError>(&e.data))
                report(name.c_str(), e.location, "SyntaxError", syntax->message);
            else
                report(name.c_str(), e.location, "TypeError", Luau::toString(e, Luau::TypeErrorToStringOptions{frontend.fileResolver}));
        }
        for (const Luau::LintWarning& w : result.lintResult.errors)
            report(path.c_str(), w.location, Luau::LintWarning::getName(w.code), w.text);
        for (const Luau::LintWarning& w : result.lintResult.warnings)
            report(path.c_str(), w.location, Luau::LintWarning::getName(w.code), w.text);
        if (!result.errors.empty() || !result.lintResult.errors.empty())
            ++failed;
    }
    return failed == 0 ? 0 : 1;
}
