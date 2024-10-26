using System.Windows;
using System.Windows.Documents;

namespace liars_bar
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(GameData gameData)
        {
            InitializeComponent();
            gameData.CaptionRuns = new Run[] { Player1Name, Player2Name, Player3Name, Player4Name, CurrentBetName };
            gameData.ValueRuns = new Run[] { Player1Values, Player2Values, Player3Values, Player4Values, CurrentBetValue };
        }
    }
}