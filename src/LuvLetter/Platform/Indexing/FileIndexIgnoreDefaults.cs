namespace LuvLetter.Platform.Indexing;

// These rules suppress rebuild triggers only; they never remove search coverage.
// Keep the shipped legacy sets intact so uncustomized configurations can upgrade
// in memory without replacing user additions, removals, or explicit empty lists.
internal static class FileIndexIgnoreDefaults
{
    private static readonly string[] LegacyDirectoryNames =
    [
        ".git", ".hg", ".svn", ".vs", ".idea",
        "node_modules", ".pnpm-store", ".yarn",
        "bin", "obj", "build", "dist", "target", "coverage", "TestResults",
        ".next", ".nuxt", ".output", ".svelte-kit", ".angular", ".turbo", ".parcel-cache",
        ".venv", "venv", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox",
        ".gradle", ".dart_tool",
    ];

    private static readonly string[] LegacyCacheDirectories =
    [
        "%USERPROFILE%\\.nuget\\packages",
        "%USERPROFILE%\\.m2\\repository",
        "%USERPROFILE%\\.cargo\\registry",
        "%USERPROFILE%\\.cargo\\git",
        "%LOCALAPPDATA%\\npm-cache",
        "%LOCALAPPDATA%\\pip\\Cache",
        "%LOCALAPPDATA%\\Yarn\\Cache",
        "%LOCALAPPDATA%\\pnpm\\store",
        "%LOCALAPPDATA%\\uv\\cache",
    ];

    internal static string[] CreateDirectoryNames() =>
    [
        .. LegacyDirectoryNames,

        // Editor settings, extension data, and agent configuration/session state.
        ".vscode", ".vscode-insiders", ".vscode-server", ".vscode-server-insiders",
        ".cursor", ".cursor-server", ".codex", ".claude", ".copilot", ".agents",

        // Dependency stores and environments. Names such as packages/vendor can
        // also contain source; only their automatic rebuild triggers are suppressed.
        ".npm", ".pnpm", ".bun", ".nuget", "nuget", "packages", ".paket",
        "vendor", ".bundle", ".ven", "__pypackages__", ".conda", ".pixi",
        ".pub-cache", ".pub",

        // Build trees and tool-generated artifacts; never blanket-ignore src/lib.
        "out", "artifacts", ".build", "_build", "CMakeFiles",
        "cmake-build-debug", "cmake-build-release", ".cxx", ".kotlin",
        ".bloop", ".bsp", ".metals", "Pods", "DerivedData", "Carthage", ".swiftpm",

        // Repeated compilation, test, and notebook caches. These remain exact
        // names: a user directory merely containing 'pycache' is not matched.
        ".cache", ".pycache", "pycache", ".nox", ".hypothesis", ".ipynb_checkpoints",
        ".pdm-build", ".sass-cache", ".nyc_output", ".vite", ".vitest", ".astro",
        ".docusaurus", ".vercel", ".netlify", ".serverless",
    ];

    internal static string[] CreateCacheDirectories() =>
    [
        .. LegacyCacheDirectories,

        // NuGet has separate global-package, HTTP, and plugin caches on Windows.
        "%LOCALAPPDATA%\\NuGet\\v3-cache",
        "%LOCALAPPDATA%\\NuGet\\plugins-cache",
        "%LOCALAPPDATA%\\NuGet\\Cache",
        "%LOCALAPPDATA%\\pypoetry\\Cache",
        "%LOCALAPPDATA%\\go-build",
        "%USERPROFILE%\\go\\pkg\\mod",
        "%USERPROFILE%\\.gradle",
        "%USERPROFILE%\\.sbt\\boot",
        "%USERPROFILE%\\.ivy2\\cache",

        // Editor data contains logs, local history, workspace databases, and
        // Copilot extension storage. Keep these anchored to the app-data root.
        "%APPDATA%\\Code",
        "%APPDATA%\\Code - Insiders",
        "%APPDATA%\\Cursor",
    ];

    internal static string[] UpgradeDirectoryNames(string[] configured) =>
        MatchesLegacy(configured, LegacyDirectoryNames) ? CreateDirectoryNames() : configured;

    internal static string[] UpgradeCacheDirectories(string[] configured) =>
        MatchesLegacy(configured, LegacyCacheDirectories) ? CreateCacheDirectories() : configured;

    private static bool MatchesLegacy(string[] configured, string[] legacy) =>
        new HashSet<string>(configured, StringComparer.OrdinalIgnoreCase).SetEquals(legacy);
}
