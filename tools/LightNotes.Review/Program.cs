using LightNotes.ReviewFixtures;

if (args.Length != 1)
    throw new ArgumentException("Supply a new review database path.");
await ReviewSamples.SeedAsync(Path.GetFullPath(args[0]));
Console.WriteLine("Created sample notes for review: " + Path.GetFullPath(args[0]));
