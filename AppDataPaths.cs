using System;
using System.Collections.Generic;
using System.IO;

namespace SDRIQStreamer.App;

/// <summary>
/// Resolves the per-user data folder under <c>%APPDATA%</c> and performs the
/// one-shot folder rename from the legacy name <c>SDRIQStreamer</c> to the
/// product-aligned name <c>SmartStreamer4</c> (issue #34).
/// </summary>
public static class AppDataPaths
{
    internal const string LegacyFolderName = "SDRIQStreamer";
    internal const string CurrentFolderName = "SmartStreamer4";

    private static string? _root;
    private static readonly List<string> _migrationMessages = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Resolved per-user data folder for this process. The path lives directly
    /// under <c>%APPDATA%</c> and is either the new <c>SmartStreamer4</c>
    /// folder (the steady-state result) or, when an in-flight rename failed
    /// because the legacy folder was locked, the legacy <c>SDRIQStreamer</c>
    /// folder for this session only — next launch retries the rename.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown if accessed before <see cref="EnsureMigrated"/> has run.
    /// </exception>
    public static string Root => _root ?? throw new InvalidOperationException(
        $"{nameof(AppDataPaths)}.{nameof(EnsureMigrated)} must be called before {nameof(Root)} is accessed.");

    /// <summary>
    /// Runs the legacy-to-current rename if it hasn't been done already and
    /// sets <see cref="Root"/>. Call once from <c>Program.Main</c> before any
    /// settings load, log file open, or other file access under the per-user
    /// data folder. Idempotent on repeated calls within the same process.
    /// </summary>
    public static void EnsureMigrated() => EnsureMigrated(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Directory.Move);

    internal static void EnsureMigrated(string appDataRoot, Action<string, string> moveDirectory)
    {
        lock (_gate)
        {
            if (_root is not null)
                return;

            var legacy = Path.Combine(appDataRoot, LegacyFolderName);
            var current = Path.Combine(appDataRoot, CurrentFolderName);

            var legacyExists = Directory.Exists(legacy);
            var currentExists = Directory.Exists(current);

            if (legacyExists && !currentExists)
            {
                try
                {
                    // Same-volume rename under %APPDATA% — atomic on Windows, no
                    // partial-copy window. Falls back to legacy on lock failure
                    // (AV scanner, Explorer holding a handle, etc).
                    moveDirectory(legacy, current);
                    _migrationMessages.Add(
                        $"Settings folder renamed: %APPDATA%\\{LegacyFolderName} -> %APPDATA%\\{CurrentFolderName}.");
                    _root = current;
                    return;
                }
                catch (Exception ex)
                {
                    _migrationMessages.Add(
                        $"Settings folder rename failed ({ex.GetType().Name}: {ex.Message}). " +
                        $"Using legacy %APPDATA%\\{LegacyFolderName} for this session; will retry next launch.");
                    _root = legacy;
                    return;
                }
            }

            if (legacyExists && currentExists)
            {
                _migrationMessages.Add(
                    $"Both %APPDATA%\\{LegacyFolderName} and %APPDATA%\\{CurrentFolderName} are present. " +
                    $"Using %APPDATA%\\{CurrentFolderName}; legacy folder left intact for manual review.");
                _root = current;
                return;
            }

            // Either only the new folder exists (already migrated, or a second
            // launch after the rename) or neither exists (fresh install). Use
            // the current path either way; nothing to migrate, nothing to log.
            _root = current;
        }
    }

    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _root = null;
            _migrationMessages.Clear();
        }
    }

    /// <summary>
    /// Returns and clears the buffered messages produced by
    /// <see cref="EnsureMigrated"/>. Call from the ViewModel during startup so
    /// the operator sees migration outcomes in the Logs tab.
    /// </summary>
    public static IReadOnlyList<string> DrainMigrationMessages()
    {
        lock (_gate)
        {
            if (_migrationMessages.Count == 0)
                return Array.Empty<string>();
            var snapshot = _migrationMessages.ToArray();
            _migrationMessages.Clear();
            return snapshot;
        }
    }
}
