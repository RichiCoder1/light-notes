namespace LightNotes.Storage;

internal interface IStorageFailureInjector
{
    void BeforeWrite(Guid noteId);
}
