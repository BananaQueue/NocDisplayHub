namespace NocDisplayHub.Core.Config;

/// <summary>An older saved version of a profile, kept when a later save replaced it.</summary>
public sealed record ProfileBackup(string Path, DateTime SavedOverAt, int BindingCount);
