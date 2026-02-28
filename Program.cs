using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NiflySharp;
using NiflySharp.Blocks;

namespace SkyrimMeshPatcher
{
    internal static class Program
    {
        // Patch values
        private const float TargetSequenceStopTime = 0.002f;
        private const float TargetKeyTime = 0.001f;

        // CLI
        // dotnet run -- --in input --out output --verify
        // dotnet run -- --open-only
        // dotnet run -- --open-only --force-shared
        public static void Main(string[] args)
        {
            var root = ProjectRoot();
            var inDir = GetArg(args, "--in", Path.Combine(root, "input"));
            var outDir = GetArg(args, "--out", Path.Combine(root, "output"));

            var verify = HasFlag(args, "--verify");
            var copyUnchanged = HasFlag(args, "--copy-unchanged");

            var openOnly = HasFlag(args, "--open-only");
            var forceShared = HasFlag(args, "--force-shared"); // only relevant with --open-only

            Directory.CreateDirectory(inDir);
            Directory.CreateDirectory(outDir);

            var files = Directory.EnumerateFiles(inDir, "*.nif", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
            {
                Console.WriteLine($"No .nif found in: {inDir}");
                return;
            }

            Console.WriteLine($"Input : {inDir}");
            Console.WriteLine($"Output: {outDir}");
            Console.WriteLine($"Files : {files.Count}");
            Console.WriteLine($"Verify: {verify}");
            Console.WriteLine($"CopyUnchanged: {copyUnchanged}");
            Console.WriteLine($"OpenOnly: {openOnly}");
            Console.WriteLine($"ForceShared: {forceShared}");
            Console.WriteLine("");

            int ok = 0, skip = 0, fail = 0;

            foreach (var inPath in files)
            {
                var rel = Path.GetRelativePath(inDir, inPath);
                var outPath = Path.Combine(outDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

                try
                {
                    var nif = new NifFile();
                    var rc = nif.Load(inPath);
                    if (rc != 0 || !nif.Valid)
                    {
                        Console.WriteLine($"[FAIL] {rel}  load rc={rc} valid={nif.Valid}");
                        fail++;
                        continue;
                    }

                    var patchStats = Patch(nif, openOnly, forceShared);

                    if (!patchStats.Changed)
                    {
                        if (copyUnchanged)
                            File.Copy(inPath, outPath, overwrite: true);

                        Console.WriteLine($"[SKIP] {rel}  (no patch targets found)");
                        skip++;
                        continue;
                    }

                    var saveRc = nif.Save(outPath);
                    if (saveRc != 0)
                    {
                        Console.WriteLine($"[FAIL] {rel}  save rc={saveRc}");
                        fail++;
                        continue;
                    }

                    if (verify)
                    {
                        var v = new NifFile();
                        var vrc = v.Load(outPath);
                        if (vrc != 0 || !v.Valid)
                        {
                            Console.WriteLine($"[FAIL] {rel}  verify load rc={vrc} valid={v.Valid}");
                            fail++;
                            continue;
                        }

                        // Basic sanity
                        var vseqs = v.Blocks.OfType<NiControllerSequence>().ToList();
                        if (patchStats.SequencesChanged > 0)
                        {
                            var hasTargetStop = vseqs.Any(s => NearlyEqual(s.StopTime, TargetSequenceStopTime));
                            if (!hasTargetStop)
                            {
                                Console.WriteLine($"[FAIL] {rel}  verify: no sequence StopTime == {TargetSequenceStopTime}");
                                fail++;
                                continue;
                            }
                        }

                        Console.WriteLine(
                            $"[ OK ] {rel}  seqChanged={patchStats.SequencesChanged} keyChanged={patchStats.KeysChanged} " +
                            $"skippedSharedData={patchStats.SharedDataSkipped} skippedNonOpenSeq={patchStats.NonOpenSeqSkipped}"
                        );
                    }
                    else
                    {
                        Console.WriteLine(
                            $"[ OK ] {rel}  seqChanged={patchStats.SequencesChanged} keyChanged={patchStats.KeysChanged} " +
                            $"skippedSharedData={patchStats.SharedDataSkipped} skippedNonOpenSeq={patchStats.NonOpenSeqSkipped}"
                        );
                    }

                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FAIL] {rel}  {ex.GetType().Name}: {ex.Message}");
                    fail++;
                }
            }

            Console.WriteLine($"\nDone. OK={ok} SKIP={skip} FAIL={fail}");
        }

        // -------------------- Patch core --------------------

        private static PatchStats Patch(NifFile nif, bool openOnly, bool forceShared)
        {
            int seqChanged = 0;
            int keysChanged = 0;
            int sharedDataSkipped = 0;
            int nonOpenSeqSkipped = 0;
            bool changed = false;

            var sequences = nif.Blocks.OfType<NiControllerSequence>().ToList();
            if (sequences.Count == 0)
                return new PatchStats(false, 0, 0, 0, 0);

            // Build: TransformDataIndex -> set of sequence indices that reference it
            // Used to avoid modifying shared data when --open-only (so Close stays untouched)
            var dataUsers = new Dictionary<int, HashSet<int>>();

            for (int si = 0; si < sequences.Count; si++)
            {
                var seq = sequences[si];
                if (seq.ControlledBlocks == null) continue;

                foreach (var cb in seq.ControlledBlocks)
                {
                    var interpRef = GetMemberValue(cb, "Interpolator");
                    var interpObj = DerefToBlock(interpRef, nif);

                    if (interpObj is not NiTransformInterpolator ti) continue;

                    var dataRef = GetMemberValue(ti, "Data");
                    var dataIdx = TryGetRefIndex(dataRef);
                    if (!dataIdx.HasValue) continue;

                    if (!dataUsers.TryGetValue(dataIdx.Value, out var set))
                    {
                        set = new HashSet<int>();
                        dataUsers[dataIdx.Value] = set;
                    }
                    set.Add(si);
                }
            }

            for (int si = 0; si < sequences.Count; si++)
            {
                var seq = sequences[si];
                var seqName = (seq.Name != null) ? (seq.Name.String ?? "") : "";

                bool patchThisSeq = !openOnly || IsOpenSequenceName(seqName);
                if (!patchThisSeq)
                {
                    nonOpenSeqSkipped++;
                    continue;
                }

                if (!NearlyEqual(seq.StopTime, TargetSequenceStopTime))
                {
                    seq.StopTime = TargetSequenceStopTime;
                    seqChanged++;
                    changed = true;
                }

                if (seq.ControlledBlocks == null) continue;

                foreach (var cb in seq.ControlledBlocks)
                {
                    var interpObj = DerefToBlock(GetMemberValue(cb, "Interpolator"), nif);
                    if (interpObj is not NiTransformInterpolator ti) continue;

                    var dataRef = GetMemberValue(ti, "Data");
                    var dataIdx = TryGetRefIndex(dataRef);

                    // If open-only: do not patch NiTransformData that is shared with other sequences (e.g. Close)
                    if (openOnly && !forceShared && dataIdx.HasValue)
                    {
                        if (dataUsers.TryGetValue(dataIdx.Value, out var users) && users.Count > 1)
                        {
                            sharedDataSkipped++;
                            continue;
                        }
                    }

                    var dataObj = DerefToBlock(dataRef, nif);
                    if (dataObj is not NiTransformData td) continue;

                    if (td.XYZRotations != null)
                    {
                        foreach (var kg in td.XYZRotations)
                        {
                            var c = PatchKeyGroup(kg);
                            if (c > 0) changed = true;
                            keysChanged += c;
                        }
                    }

                    {
                        var c = PatchKeyGroup(td.Translations);
                        if (c > 0) changed = true;
                        keysChanged += c;
                    }
                    {
                        var c = PatchKeyGroup(td.Scales);
                        if (c > 0) changed = true;
                        keysChanged += c;
                    }
                }
            }

            return new PatchStats(changed, seqChanged, keysChanged, sharedDataSkipped, nonOpenSeqSkipped);
        }

        // Heuristic: "Open" should match, "Close" should not.
        private static bool IsOpenSequenceName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            var n = name.Trim();

            // Common patterns in NIF sequences: Open / Open01 / OpenStart / etc.
            // Also ensure we don't catch "Close" variants.
            return n.Contains("open", StringComparison.OrdinalIgnoreCase)
                   && !n.Contains("close", StringComparison.OrdinalIgnoreCase);
        }

        // Patch KeyGroup<T>.Keys: set all key.Time > 0 to TargetKeyTime.
        // Handles both class keys and struct keys (structs require write-back by index).
        private static int PatchKeyGroup(object? keyGroup)
        {
            if (keyGroup == null) return 0;

            var keysObj = GetMemberValue(keyGroup, "Keys") ?? GetMemberValue(keyGroup, "keys");
            if (keysObj == null) return 0;

            if (keysObj is IList list)
            {
                int changed = 0;

                for (int i = 0; i < list.Count; i++)
                {
                    var elem = list[i];
                    if (elem == null) continue;

                    var oldTime = GetKeyTime(elem);
                    if (!oldTime.HasValue) continue;

                    if (oldTime.Value > 0f && !NearlyEqual(oldTime.Value, TargetKeyTime))
                    {
                        if (elem.GetType().IsValueType)
                        {
                            var boxed = elem; // boxed struct
                            if (SetKeyTime(ref boxed, TargetKeyTime))
                            {
                                list[i] = boxed;
                                changed++;
                            }
                        }
                        else
                        {
                            var obj = elem;
                            if (SetKeyTime(ref obj, TargetKeyTime))
                            {
                                list[i] = obj;
                                changed++;
                            }
                        }
                    }
                }

                return changed;
            }

            // Fallback (may not write back for value types)
            int changedFallback = 0;
            foreach (var key in Enumerate(keysObj))
            {
                if (key == null) continue;
                var t = GetKeyTime(key);
                if (!t.HasValue) continue;
                if (t.Value > 0f && !NearlyEqual(t.Value, TargetKeyTime))
                {
                    var k = key;
                    if (SetKeyTime(ref k, TargetKeyTime))
                        changedFallback++;
                }
            }
            return changedFallback;
        }

        // -------------------- NIF deref helpers --------------------

        private static int? TryGetRefIndex(object? refObj)
        {
            if (refObj == null) return null;

            var t = refObj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            foreach (var name in new[] { "Index", "index", "BlockIndex", "blockIndex", "RefIndex", "refIndex", "Id", "id" })
            {
                var p = t.GetProperty(name, flags);
                if (p != null && p.PropertyType == typeof(int))
                {
                    try { return (int?)p.GetValue(refObj); } catch { }
                }

                var f = t.GetField(name, flags);
                if (f != null && f.FieldType == typeof(int))
                {
                    try { return (int?)f.GetValue(refObj); } catch { }
                }
            }

            return null;
        }

        // Deref NiBlockRef<T>/NiBlockPtr<T> to actual block object in nif.Blocks.
        private static object? DerefToBlock(object? refObj, NifFile nif)
        {
            if (refObj == null) return null;

            if (refObj is NiObject) return refObj;

            var idx = TryGetRefIndex(refObj);
            if (idx.HasValue && idx.Value >= 0 && idx.Value < nif.Blocks.Count)
                return nif.Blocks[idx.Value];

            var t = refObj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            foreach (var name in new[] { "Value", "value", "Target", "target", "Block", "block", "Object", "object" })
            {
                var p = t.GetProperty(name, flags);
                if (p != null)
                {
                    var v = SafeGet(() => p.GetValue(refObj));
                    if (v != null) return v;
                }

                var f = t.GetField(name, flags);
                if (f != null)
                {
                    var v = SafeGet(() => f.GetValue(refObj));
                    if (v != null) return v;
                }
            }

            foreach (var mname in new[] { "Resolve", "Get", "GetValue", "GetBlock" })
            {
                var m = t.GetMethods(flags).FirstOrDefault(m =>
                {
                    if (!string.Equals(m.Name, mname, StringComparison.Ordinal)) return false;
                    var ps = m.GetParameters();
                    return ps.Length == 1 && ps[0].ParameterType == typeof(NifFile);
                });

                if (m != null)
                {
                    var v = SafeGet(() => m.Invoke(refObj, new object[] { nif }));
                    if (v != null) return v;
                }
            }

            return null;
        }

        private static object? GetMemberValue(object obj, string name)
        {
            var t = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            var p = t.GetProperty(name, flags);
            if (p != null)
            {
                try { return p.GetValue(obj); } catch { }
            }

            var f = t.GetField(name, flags);
            if (f != null)
            {
                try { return f.GetValue(obj); } catch { }
            }

            return null;
        }

        // -------------------- Key Time read/write --------------------

        private static IEnumerable<object?> Enumerate(object obj)
        {
            if (obj is string) yield break;

            if (obj is IEnumerable e)
            {
                foreach (var it in e) yield return it;
                yield break;
            }

            yield return obj;
        }

        private static float? GetKeyTime(object keyObj)
        {
            var t = keyObj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            foreach (var name in new[] { "Time", "time" })
            {
                var p = t.GetProperty(name, flags);
                if (p != null)
                {
                    var v = SafeGet(() => p.GetValue(keyObj));
                    if (v is float f) return f;
                    if (v is double d) return (float)d;
                }

                var f2 = t.GetField(name, flags);
                if (f2 != null)
                {
                    var v = SafeGet(() => f2.GetValue(keyObj));
                    if (v is float f) return f;
                    if (v is double d) return (float)d;
                }
            }

            return null;
        }

        // Mutates object or boxed struct; returns true if set succeeded.
        private static bool SetKeyTime(ref object keyObj, float newTime)
        {
            var t = keyObj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

            foreach (var name in new[] { "Time", "time" })
            {
                var p = t.GetProperty(name, flags);
                if (p != null && p.CanWrite && (p.PropertyType == typeof(float) || p.PropertyType == typeof(double)))
                {
                    try
                    {
                        if (p.PropertyType == typeof(float)) p.SetValue(keyObj, newTime);
                        else p.SetValue(keyObj, (double)newTime);
                        return true;
                    }
                    catch { }
                }

                var f = t.GetField(name, flags);
                if (f != null && (f.FieldType == typeof(float) || f.FieldType == typeof(double)))
                {
                    try
                    {
                        if (f.FieldType == typeof(float)) f.SetValue(keyObj, newTime);
                        else f.SetValue(keyObj, (double)newTime);
                        return true;
                    }
                    catch { }
                }
            }

            return false;
        }

        // -------------------- misc --------------------

        private static object? SafeGet(Func<object?> f)
        {
            try { return f(); } catch { return null; }
        }

        private static bool NearlyEqual(float a, float b, float eps = 1e-6f) =>
            MathF.Abs(a - b) <= eps;

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        private static string GetArg(string[] args, string name, string fallback)
        {
            var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return (idx >= 0 && idx + 1 < args.Length) ? args[idx + 1] : fallback;
        }

        private static bool HasFlag(string[] args, string name) =>
            args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private readonly record struct PatchStats(
            bool Changed,
            int SequencesChanged,
            int KeysChanged,
            int SharedDataSkipped,
            int NonOpenSeqSkipped
        );
    }
}
