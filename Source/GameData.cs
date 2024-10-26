using System.Windows.Documents;

namespace liars_bar
{
    public class GameData {
        private Run[] captionRuns;
        private Run[] valueRuns;

        public Run[] CaptionRuns {
            get {
                return captionRuns;
            }
            set {
                captionRuns = value;
            }
        }

        public Run[] ValueRuns {
            get {
                return valueRuns;
            }
            set {
                valueRuns = value;
            }
        }
    }
}