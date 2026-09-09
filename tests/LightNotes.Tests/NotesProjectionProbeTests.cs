using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LightNotes.ReviewFixtures;
using Lucent.Core;
using Lucent.Renderer.Skia;

namespace LightNotes.Tests;

public sealed partial class ShellPresentationTests
{
    private const int ProjectionProbeWarmupSamples = 5;
    private const int ProjectionProbeSamples = 20;

    [TestMethod]
    public void OptInNotesProjectionProbe()
    {
        if (
            !string.Equals(
                Environment.GetEnvironmentVariable("LIGHT_NOTES_PROJECTION_PROBE"),
                "1",
                StringComparison.Ordinal
            )
        )
        {
            Assert.Inconclusive(
                "Set LIGHT_NOTES_PROJECTION_PROBE=1 to run the bounded projection characterization."
            );
        }

        using var fixture = new Fixture();
        var model = fixture.Model;
        fixture.Pump(model.StartAsync());
        Assert.IsTrue(model.IsReady, "The synthetic Notes workspace did not become ready.");

        using var composition = new Composition(fixture.Graph, "light-notes-projection-probe");
        composition.ConfigureImages(new ImageCache(new SkiaImagePreparer()));
        using var theme = new ThemeContext(composition.Root.Scope, LightNotesTheme.Create());
        composition.Mount(composition.Root, theme, global::LightNotes.Components.AppView(model));
        using var renderer = new SkiaSceneRenderer();

        var readyScene = Scenario(
            fixture,
            composition,
            renderer,
            model,
            "projection-ready",
            1180,
            760,
            1,
            export: false,
            minimumReadyImages: 1
        );
        var readyImageCount = SceneNodes(readyScene.Nodes).OfType<ImageSceneNode>().Count();
        var observations = new List<NotesProjectionObservation>
        {
            MeasureProjection(
                fixture,
                composition,
                renderer,
                "notes-wide-unchanged",
                _ => new(1180, 760, 1),
                readyScene
            ),
            MeasureProjection(
                fixture,
                composition,
                renderer,
                "notes-same-wide-bucket",
                index => new(index % 2 == 0 ? 1100 : 1180, 760, 1)
            ),
            MeasureProjection(
                fixture,
                composition,
                renderer,
                "notes-breakpoint-change",
                index => new(index % 2 == 0 ? 800 : 1120, 760, 1)
            ),
            MeasureProjection(
                fixture,
                composition,
                renderer,
                "notes-medium-narrow-change",
                index => new(index % 2 == 0 ? 800 : 900, 760, 1)
            ),
        };

        var report = new NotesProjectionReport(
            Environment.GetEnvironmentVariable("LIGHT_NOTES_SOURCE_COMMIT") ?? "unset",
            Environment.GetEnvironmentVariable("LIGHT_NOTES_SOURCE_DIRTY_HASH") ?? "unset",
            Environment.GetEnvironmentVariable("LIGHT_NOTES_ARTWORK_HASH") ?? "unset",
            Environment.GetEnvironmentVariable("LIGHT_NOTES_PROBE_HASH") ?? "unset",
            AssemblyInformationalVersion(typeof(NoteWorkspace).Assembly),
            AssemblyInformationalVersion(typeof(Composition).Assembly),
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            "Release",
            ProjectionProbeWarmupSamples,
            ProjectionProbeSamples,
            readyImageCount,
            observations
        );
        var json = JsonSerializer.Serialize(
            report,
            NotesProjectionJsonContext.Default.NotesProjectionReport
        );
        Console.WriteLine(json);
        if (
            Environment.GetEnvironmentVariable("LIGHT_NOTES_PROJECTION_ARTIFACTS") is
            { Length: > 0 } directory
        )
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "notes-projection.json"), json);
        }
    }

    private static NotesProjectionObservation MeasureProjection(
        Fixture fixture,
        Composition composition,
        SkiaSceneRenderer renderer,
        string scenario,
        Func<int, LayoutViewport> viewport,
        RetainedScene? seed = null
    )
    {
        RetainedScene? current = seed;
        try
        {
            for (var index = 0; index < ProjectionProbeWarmupSamples; index++)
                _ = ProjectAccepted(fixture, composition, renderer, viewport(index), ref current);

            var elapsed = new double[ProjectionProbeSamples];
            var allocations = new long[ProjectionProbeSamples];
            var attempts = new int[ProjectionProbeSamples];
            var boxes = 0;
            var rows = 0;
            for (var index = 0; index < ProjectionProbeSamples; index++)
            {
                var sample = ProjectAccepted(
                    fixture,
                    composition,
                    renderer,
                    viewport(index),
                    ref current
                );
                elapsed[index] = sample.ElapsedMilliseconds;
                allocations[index] = sample.AllocatedBytes;
                attempts[index] = sample.Attempts;
                boxes = sample.BoxCount;
                rows = sample.RowCount;
            }
            Array.Sort(elapsed);
            Array.Sort(allocations);
            return new(
                scenario,
                Descendants(composition.SemanticSnapshot()).Count(),
                boxes,
                rows,
                attempts.Sum(value => value - 1),
                attempts.Max(),
                elapsed.Average(),
                Percentile(elapsed, 0.5),
                Percentile(elapsed, 0.95),
                allocations.Average(),
                Percentile(allocations, 0.5),
                Percentile(allocations, 0.95)
            );
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static ProjectionProbeSample ProjectAccepted(
        Fixture fixture,
        Composition composition,
        SkiaSceneRenderer renderer,
        LayoutViewport viewport,
        ref RetainedScene? current
    )
    {
        var elapsed = 0d;
        var allocated = 0L;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            fixture.Drain();
            composition.Flush();
            var allocationStart = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            var scene = SceneLayout.Project(composition, viewport, renderer);
            elapsed += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - allocationStart;
            var boxCount = scene.Boxes.Count;
            if (composition.Input.SetScene(scene))
            {
                current?.Dispose();
                current = scene;
                var rowCount = Descendants(composition.SemanticSnapshot())
                    .Count(node => node.Role == SemanticRole.ListItem);
                return new(elapsed, allocated, attempt, boxCount, rowCount);
            }
            scene.Dispose();
        }
        throw new InvalidOperationException(
            "Notes projection probe scene ownership was rejected four consecutive times."
        );
    }

    private static string AssemblyInformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private static long Percentile(long[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private sealed record ProjectionProbeSample(
        double ElapsedMilliseconds,
        long AllocatedBytes,
        int Attempts,
        int BoxCount,
        int RowCount
    );

    internal sealed record NotesProjectionObservation(
        string Scenario,
        int SemanticElements,
        int BoxCount,
        int RowCount,
        int OwnershipRetries,
        int MaximumAttempts,
        double AverageMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double AverageAllocatedBytes,
        long MedianAllocatedBytes,
        long P95AllocatedBytes
    );

    internal sealed record NotesProjectionReport(
        string SourceCommit,
        string SourceDirtyHash,
        string ArtworkHash,
        string ProbeHash,
        string LightNotesAssembly,
        string LucentCoreAssembly,
        string Machine,
        string OperatingSystem,
        string Framework,
        string Architecture,
        string Configuration,
        int WarmupSamples,
        int Samples,
        int ReadyImageCount,
        IReadOnlyList<NotesProjectionObservation> Observations
    );
}

[JsonSerializable(typeof(ShellPresentationTests.NotesProjectionReport))]
internal sealed partial class NotesProjectionJsonContext : JsonSerializerContext { }
