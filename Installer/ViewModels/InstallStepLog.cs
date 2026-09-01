namespace KalOS.Setup.ViewModels
{
    /// <summary>
    /// One completed or failed step in the wizard's install log, shown in the
    /// Progress page's step list. Top-level (not nested) so x:Bind can resolve
    /// it as an <c>x:DataType</c> (nested records use the <c>Outer+Inner</c>
    /// syntax which the XAML compiler cannot resolve).
    /// </summary>
    public sealed record InstallStepLog(string Name, bool Success, string? Detail, bool Skipped = false);
}
