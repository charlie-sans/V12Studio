namespace V12Studio.Core
{
    /// <summary>
    /// Editor service registered into the V12 registry by the studio pak.
    /// Host applications resolve this after the runtime starts and drive all
    /// UI against <see cref="Session"/>; the pak itself builds no UI.
    /// </summary>
    public interface IEditor
    {
        /// <summary>The host-agnostic editor session: scene world, selection,
        /// scene ops, and the console log.</summary>
        StudioSession Session { get; }
    }
}
