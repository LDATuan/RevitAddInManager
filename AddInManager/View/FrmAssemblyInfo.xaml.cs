using System.Windows;
using RevitAddinManager.ViewModel;

namespace RevitAddinManager.View;

public partial class FrmAssemblyInfo : Window
{
    public FrmAssemblyInfo(AddInManagerViewModel vm)
    {
        InitializeComponent();
        Model.ThemManager.Apply(this);
        this.DataContext = vm;
    }
}