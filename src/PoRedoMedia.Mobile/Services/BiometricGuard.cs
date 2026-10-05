namespace PoRedoMedia.Mobile.Services;

/// <summary>
/// Hardware-biometric gate for the private gallery — fingerprint/face via the platform keystore.
/// A browser page cannot gate storage on device-held biometrics; that is the whole point.
/// </summary>
public interface IBiometricGuard
{
    /// <summary>True when the device has enrolled biometrics (or a lock-screen credential).</summary>
    Task<bool> IsAvailableAsync();

    /// <summary>
    /// Shows the platform biometric prompt. True only when the user passed.
    /// </summary>
    Task<bool> UnlockAsync(string reason);
}

/// <summary>
/// The no-op used off Android: reports unavailable so the lock never engages and nobody is
/// stranded in front of a gate that cannot open.
/// </summary>
public sealed class NullBiometricGuard : IBiometricGuard
{
    public Task<bool> IsAvailableAsync() => Task.FromResult(false);
    public Task<bool> UnlockAsync(string reason) => Task.FromResult(false);
}
