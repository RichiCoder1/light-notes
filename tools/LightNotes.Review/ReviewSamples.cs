using System.IO;
using LightNotes.Storage;

namespace LightNotes.ReviewFixtures;

internal static class ReviewSamples
{
    internal static async Task SeedAsync(string databasePath)
    {
        if (File.Exists(databasePath))
            throw new IOException(
                "Review seeding requires a new database; existing notes are never replaced."
            );
        await using var store = await NoteStore.OpenAsync(databasePath).ConfigureAwait(false);
        for (var index = 1; index <= 18; index++)
            await store
                .SaveAsync(
                    new(
                        Guid.NewGuid(),
                        NoteKind.Note,
                        $"Reading list — idea {index:00}",
                        null,
                        "An example note for checking similar titles, filtering, and list scrolling."
                    )
                )
                .ConfigureAwait(false);
        var archive = await store
            .SaveAsync(
                new(
                    Guid.NewGuid(),
                    NoteKind.Note,
                    "A finished experiment",
                    null,
                    "Archived sample. Restore it to try the return trip."
                )
            )
            .ConfigureAwait(false);
        await store.ArchiveAsync(archive.Id, true).ConfigureAwait(false);
        await store
            .SaveAsync(
                new(
                    Guid.NewGuid(),
                    NoteKind.Link,
                    "An unusually long article title about keeping small desktop tools comfortable as their content grows beyond the first screen and into everyday use",
                    "https://example.com/articles/"
                        + new string('a', 320)
                        + "?context=desktop-layout-review&source=light-notes",
                    "Sample long URL. Its complete value should remain editable and accessible."
                )
            )
            .ConfigureAwait(false);
        var paragraph =
            "A quiet place to think. Keep the useful detail and leave room for the next idea.\n\nUnicode samples: café, e\u0301, 👩🏽‍💻, العربية, עברית.\n\tA tab-indented line.\n\n";
        var longBody = string.Concat(Enumerable.Repeat(paragraph, 160));
        longBody = longBody[..(char.IsHighSurrogate(longBody[19_999]) ? 19_999 : 20_000)];
        await store
            .SaveAsync(
                new(
                    Guid.NewGuid(),
                    NoteKind.Note,
                    "Long note — reading and editing at 20,000 characters",
                    null,
                    longBody
                )
            )
            .ConfigureAwait(false);
        await store
            .SaveAsync(
                new(
                    Guid.NewGuid(),
                    NoteKind.Link,
                    "SQLite is transactional",
                    "https://sqlite.org/transactional.html",
                    "Keep the persistence story small and predictable.\n\nA saved note should still be here after the app closes."
                )
            )
            .ConfigureAwait(false);
        await store
            .SaveAsync(
                new(
                    Guid.NewGuid(),
                    NoteKind.Note,
                    "Questions for the next quiet afternoon",
                    null,
                    "What would make this useful every day?\n\n• Capture a thought without losing the one I am editing.\n• Find a link by the words I remember.\n• Make the small window feel intentional.\n\nTry editing this note, narrowing the window, and coming back."
                )
            )
            .ConfigureAwait(false);
    }
}
