using System.Text.Json;
using LightNotes.Storage;

namespace LightNotes.Tests;

[TestClass]
public sealed class MaintenanceTests
{
    [TestMethod]
    [DataRow("--backup", false)]
    [DataRow("--backup", true)]
    [DataRow("--export", false)]
    [DataRow("--export", true)]
    public async Task MissingMaintenanceSourceFailsWithoutCreatingFiles(
        string operation,
        bool sourceDirectoryExists
    )
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "LightNotes.Maintenance.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            var sourceDirectory = Path.Combine(root, "source");
            if (sourceDirectoryExists)
                Directory.CreateDirectory(sourceDirectory);
            var destination = Path.Combine(root, "output", "copy");
            var result = await Program.MaintainAsync(
                [operation, destination],
                Path.Combine(sourceDirectory, "notes.db")
            );
            Assert.AreEqual(1, result);
            Assert.AreEqual(sourceDirectoryExists, Directory.Exists(sourceDirectory));
            Assert.IsFalse(File.Exists(destination));
            CollectionAssert.AreEqual(
                Array.Empty<string>(),
                Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            );
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(destination)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("--backup")]
    [DataRow("--export")]
    public async Task ExistingMaintenanceSourceProducesDurableRecords(string operation)
    {
        using var temp = new MaintenanceDirectory();
        var databasePath = Path.Combine(temp.Path, "notes.db");
        var id = Guid.NewGuid();
        await using (var seed = await NoteStore.OpenAsync(databasePath))
            await seed.SaveAsync(new(id, NoteKind.Note, "Kept title", null, "Kept body", 0));
        var original = await File.ReadAllBytesAsync(databasePath);
        var destination = Path.Combine(temp.Path, "output", "copy");
        Assert.AreEqual(0, await Program.MaintainAsync([operation, destination], databasePath));
        if (operation == "--backup")
        {
            await using var backup = await NoteStore.OpenExistingAsync(destination);
            var record = await backup.GetAsync(id);
            Assert.IsNotNull(record);
            Assert.AreEqual("Kept title", record.Title);
            Assert.AreEqual("Kept body", record.Body);
        }
        else
        {
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(destination));
            var notes = document.RootElement.GetProperty("Notes");
            Assert.AreEqual(1, notes.GetArrayLength());
            Assert.AreEqual(id, notes[0].GetProperty("Id").GetGuid());
            Assert.AreEqual("Kept title", notes[0].GetProperty("Title").GetString());
            Assert.AreEqual("Kept body", notes[0].GetProperty("Body").GetString());
        }
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(databasePath));
    }

    [TestMethod]
    [DataRow("--backup")]
    [DataRow("--export")]
    public async Task InvalidMaintenanceSourceFailsWithoutInitializingIt(string operation)
    {
        using var temp = new MaintenanceDirectory();
        var databasePath = Path.Combine(temp.Path, "notes.db");
        await File.WriteAllBytesAsync(databasePath, []);
        var destination = Path.Combine(temp.Path, "output", "copy");
        Assert.AreEqual(1, await Program.MaintainAsync([operation, destination], databasePath));
        Assert.AreEqual(0, (await File.ReadAllBytesAsync(databasePath)).Length);
        CollectionAssert.AreEqual(
            new[] { databasePath },
            Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories)
        );
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(destination)));
    }

    private sealed class MaintenanceDirectory : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "LightNotes.Maintenance.Tests",
                Guid.NewGuid().ToString("N")
            );

        public MaintenanceDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
