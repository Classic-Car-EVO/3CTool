using ThreeCTool.Feeder.Contracts;
using ThreeCTool.Streaming.Recovery;

namespace ThreeCTool.Feeder.Service.Jobs;

/// <summary>
/// The single operator prompt shown on the touch screen (program pauses, spindle dial confirmations and recovery
/// steps). Answers come only from the local UI.
/// </summary>
public sealed class PromptBroker : IOperatorInteraction
{
    private readonly Lock gate = new();
    private Prompt? current;
    private TaskCompletionSource<PromptAnswer>? pending;

    public event EventHandler? Changed;

    public Prompt? Current
    {
        get
        {
            lock (gate)
            {
                return current?.Clone();
            }
        }
    }

    /// <summary>Shows <paramref name="prompt"/> (replacing nothing: one prompt at a time) and waits for the answer.</summary>
    public async Task<PromptAnswer> AskAsync(Prompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var tcs = new TaskCompletionSource<PromptAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (current is not null)
            {
                throw new InvalidOperationException("Another operator prompt is open.");
            }

            prompt = prompt.Clone();
            prompt.Id = Guid.NewGuid().ToString("N");
            current = prompt;
            pending = tcs;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Clear(prompt.Id);
        }
    }

    public async Task<bool> ConfirmAsync(string message, CancellationToken cancellationToken)
    {
        var answer = await AskAsync(new Prompt { Kind = PromptKind.Confirm, Message = message, CanCancel = true }, cancellationToken).ConfigureAwait(false);
        return answer.Confirmed;
    }

    public void Answer(PromptAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        TaskCompletionSource<PromptAnswer>? tcs;
        lock (gate)
        {
            if (current is null || current.Id != answer.Id)
            {
                throw new KeyNotFoundException("The prompt is no longer open.");
            }

            if (!answer.Confirmed && !current.CanCancel)
            {
                throw new ArgumentException("This prompt cannot be cancelled.");
            }

            if (current.Kind == PromptKind.Spindle && answer.Confirmed && !(answer.Rpm >= 0 && double.IsFinite(answer.Rpm)))
            {
                throw new ArgumentException("Spindle RPM must be a positive number (0 = as suggested).");
            }

            tcs = pending;
        }

        tcs?.TrySetResult(answer);
    }

    private void Clear(string id)
    {
        lock (gate)
        {
            if (current?.Id != id)
            {
                return;
            }

            current = null;
            pending = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
