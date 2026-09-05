#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HumanoidHandRetargeter.Target;

namespace HumanoidHandRetargeter.Editor;

public sealed record VmdlCommitResult(IReadOnlyList<string> WrittenFiles, string? BackupPath);

/// <summary>Editor IO boundary for prepared ModelDoc changes. Writes dependencies before
/// the VMDL, atomically replaces each file, then requires engine compilation/validation.
/// On failure restores previous bytes; asset watchers can recompile the restored files.</summary>
public static class VmdlSetupTransaction
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<VmdlCommitResult> CommitAsync(string assetsDirectory, string modelPath,
        string expectedVmdl, VmdlSetupResult prepared, IReadOnlyDictionary<string, string> animations,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>> compileAndValidate,
        bool backupExisting = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(compileAndValidate);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsDirectory));
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
            var model = Resolve(modelPath, ".vmdl");
            var modelBytes = File.ReadAllBytes(model);
            using var reader = new StreamReader(new MemoryStream(modelBytes), Encoding.UTF8, true);
            if (reader.ReadToEnd() != expectedVmdl)
                throw new IOException("The target VMDL changed after setup was prepared. Inspect it again before saving.");
            var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in animations)
                writes.Add(Resolve(path, ".dmx"), Encoding.UTF8.GetBytes(content));
            foreach (var animation in prepared.Animations)
            {
                var path = Resolve(animation.SourceFilename, ".dmx");
                if (!writes.ContainsKey(path) && !File.Exists(path))
                    throw new FileNotFoundException($"Animation '{animation.SequenceName}' has no source file.", path);
            }
            if (prepared.GeneratedGraphPath is not null)
            {
                var graph = Resolve(prepared.GeneratedGraphPath, ".vanmgrph");
                var bytes = Encoding.UTF8.GetBytes(prepared.GeneratedGraphText
                    ?? throw new ArgumentException("Generated graph content is missing."));
                if (File.Exists(graph) && !File.ReadAllBytes(graph).SequenceEqual(bytes))
                    throw new IOException($"A different graph already exists at '{prepared.GeneratedGraphPath}'. It was preserved.");
                writes.Add(graph, bytes);
            }
            // Dictionary insertion order leaves the owner VMDL last, after its inputs.
            writes.Add(model, prepared.Changed ? Encoding.UTF8.GetBytes(prepared.VmdlText) : modelBytes);
            var previous = writes.Keys.ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null,
                StringComparer.OrdinalIgnoreCase);
            previous[model] = modelBytes;
            if (prepared.GeneratedGraphPath is { } generatedPath)
            {
                var graph = Resolve(generatedPath, ".vanmgrph");
                if (previous[graph] is { } existing && !existing.SequenceEqual(writes[graph]))
                    throw new IOException("The generated graph path was changed while preparing the save.");
            }
            var changed = writes.Where(p => previous[p.Key] is null || !previous[p.Key]!.SequenceEqual(p.Value)).ToArray();
            var installed = new List<string>();
            string? backup = null;
            try
            {
                foreach (var (path, bytes) in changed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Check again immediately before every replacement, including the
                    // VMDL after dependencies. Never silently replace a concurrent edit.
                    var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    if (!Equal(current, previous[path])) throw new IOException($"'{path}' changed while saving.");
                    if (path == model && backupExisting)
                    {
                        backup = model + "." + Guid.NewGuid().ToString("N") + ".bak";
                        File.WriteAllBytes(backup, previous[path]!);
                    }
                    AtomicWrite(path, bytes);
                    installed.Add(path);
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Even an unchanged setup must validate: a missing/stale compiled asset
                // cannot turn an idempotent source edit into a false success.
                if (!await compileAndValidate(writes.Keys.ToArray(), cancellationToken))
                    throw new InvalidOperationException("The configured model failed engine compilation or validation.");
                cancellationToken.ThrowIfCancellationRequested();
                return new(installed.AsReadOnly(), backup);
            }
            catch (Exception failure)
            {
                var errors = new List<Exception> { failure };
                foreach (var path in installed.AsEnumerable().Reverse())
                {
                    try
                    {
                        var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
                        if (!Equal(current, writes[path]))
                            throw new IOException($"Rollback preserved a concurrent edit to '{path}'.");
                        if (previous[path] is { } bytes) AtomicWrite(path, bytes);
                        else File.Delete(path);
                    }
                    catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
                }
                if (errors.Count > 1) throw new AggregateException("Setup failed and some files could not be restored.", errors);
                throw;
            }

            string Resolve(string relative, string extension)
            {
                relative = VmdlSetupService.NormalizeAssetPath(relative, extension);
                var path = Path.GetFullPath(Path.Combine(root, relative));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Output escapes the project's Assets directory.");
                // Do not let a nested junction redirect a project write elsewhere.
                for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent is not null
                    && !string.Equals(parent.FullName, root, StringComparison.OrdinalIgnoreCase); parent = parent.Parent)
                    if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"Output folder '{parent.FullName}' is a link; choose a physical project folder.");
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Output '{path}' is a link.");
                return path;
            }
        }
        finally { Gate.Release(); }
    }

    private static bool Equal(byte[]? a, byte[]? b)
        => a is null ? b is null : b is not null && a.SequenceEqual(b);

    private static void AtomicWrite(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
