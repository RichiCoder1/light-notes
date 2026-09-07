namespace LightNotes.Storage;

internal interface IStorageFailureInjector
{
    void BeforeWrite(Guid noteId);
}

internal interface IStorageRecoveryFailureInjector
{
    void BeforePublish(string temporaryPath);
}
