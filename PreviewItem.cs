using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PcCleaner;
public sealed class PreviewItem : INotifyPropertyChanged
{
    private FileFeedback feedback;
    private bool included;
    public PreviewItem(FileFeedback feedback, bool included) { this.feedback = feedback; this.included = included; }
    public string Path => feedback.Path;
    public string Category => feedback.Category;
    public string Size => feedback.Size;
    public string Modified => feedback.Modified;
    public bool CanSelect => feedback.Status == "Eligible";
    public bool Include
    {
        get => included && CanSelect;
        set { if (included == value) return; included = value; Notify(); Notify(nameof(Status)); Notify(nameof(Reason)); Notify(nameof(Color)); Notify(nameof(Tooltip)); }
    }
    public string Status => CanSelect && !included ? "Excluded" : feedback.Status;
    public string Reason => CanSelect && !included ? "Excluded by you; this file will be kept." : feedback.Reason;
    public string Color => Status == "Excluded" ? "#315DD9" : feedback.Color;
    public string Tooltip => $"{Path}\n{Status}: {Reason}\n{Size} · Modified {Modified}";
    public void Apply(FileFeedback value)
    {
        feedback = value.ModifiedUtc == null ? value with { Bytes = feedback.Bytes, ModifiedUtc = feedback.ModifiedUtc } : value;
        foreach (var name in new[] { nameof(Include), nameof(CanSelect), nameof(Status), nameof(Reason), nameof(Color), nameof(Size), nameof(Modified), nameof(Tooltip) }) Notify(name);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
