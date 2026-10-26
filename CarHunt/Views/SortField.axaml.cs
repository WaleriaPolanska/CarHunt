using Avalonia.Controls;

namespace CarHunt.Views;

public partial class SortField : UserControl
{
    public SortField()
    {
        InitializeComponent();
        PressFeedback.Attach(Field, PressFeedback.Quick);
    }
}
