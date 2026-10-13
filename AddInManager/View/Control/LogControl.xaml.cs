using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitAddinManager.Model;
using RevitAddinManager.ViewModel;
using Clipboard = System.Windows.Clipboard;
using UserControl = System.Windows.Controls.UserControl;

namespace RevitAddinManager.View.Control
{
    /// <summary>
    /// Interaction logic for LogControl.xaml
    /// </summary>
    public partial class LogControl : UserControl
    {
        /// <summary>The most recently created log control (the one new messages scroll into view).</summary>
        public static LogControl Current { get; private set; }

        public LogControl()
        {
            InitializeComponent();
            ThemManager.Apply(this);
            ThemManager.ThemeChanged += () => ThemManager.Apply(this);
            LogControlViewModel viewModel = new LogControlViewModel();
            DataContext = viewModel;
            Current = this;
            this.Loaded += viewModel.LogFileWatcher;
            this.Unloaded += viewModel.UserControl_Unloaded;

        }
        private void RightClickCopyCmdExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            StringBuilder sb = new StringBuilder();
            foreach (LogMessageString dd in listBox_LogMessages.SelectedItems)
            {
                sb.AppendLine(dd.Message);
            }
            Clipboard.SetText(sb.ToString());
        }
        private void RightClickCopyCmdCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            e.CanExecute = listBox_LogMessages.SelectedItem != null;
        }
    }
}
