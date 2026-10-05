using AndroidX.Biometric;
using AndroidX.Core.Content;

namespace PoRedoMedia.Mobile.Platforms.Android;

/// <summary>
/// AndroidX BiometricPrompt-backed gallery lock. Class-based (fingerprint/face) plus device
/// credential fallback, so a phone without enrolled prints still opens via its PIN.
/// </summary>
public sealed class BiometricGuard : Services.IBiometricGuard
{
    private const int Authenticators =
        BiometricManager.Authenticators.BiometricWeak
        | BiometricManager.Authenticators.DeviceCredential;

    public Task<bool> IsAvailableAsync()
    {
        var manager = BiometricManager.From(Platform.AppContext);
        var can = manager.CanAuthenticate(Authenticators);
        return Task.FromResult(can == BiometricManager.BiometricSuccess);
    }

    public Task<bool> UnlockAsync(string reason)
    {
        var activity = Platform.CurrentActivity as AndroidX.Fragment.App.FragmentActivity;
        if (activity is null)
            return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var prompt = new BiometricPrompt(
            activity,
            ContextCompat.GetMainExecutor(activity)!,
            new AuthCallback(completion));

        var info = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle("Unlock PoRedo")
            .SetSubtitle(reason)
            .SetAllowedAuthenticators(Authenticators)
            .Build();

        prompt.Authenticate(info);
        return completion.Task;
    }

    private sealed class AuthCallback(TaskCompletionSource<bool> completion)
        : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) =>
            completion.TrySetResult(true);

        public override void OnAuthenticationError(int errorCode, global::Java.Lang.ICharSequence errString) =>
            completion.TrySetResult(false);

        // OnAuthenticationFailed (a bad read) deliberately does nothing: the prompt stays up and
        // lets the user retry, which is better UX than a red flash per attempt.
    }
}
