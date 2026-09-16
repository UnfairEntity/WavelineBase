using UnityEngine.UIElements;

namespace Menu.Components
{
    [UxmlElement]
    public partial class LobbyButton : Button
    {
        // STYLE
        private const string ClassName = "lobby-button";
        private const string NameTextClassName = "lobby-button__name-text";
        private const string PlayersTextClassName = "lobby-button__players-text";

        private readonly Label _nameText;
        private readonly Label _playersText;

        private string _sessionName;
        private int _playerCount;
        private int _maxPlayers;
        
        [UxmlAttribute]
        public string SessionName
        {
            get => _sessionName;
            set
            {
                _sessionName = value;
                _nameText.text = value;
            }
        }

        [UxmlAttribute]
        public int PlayerCount 
        {
            get => _playerCount;
            set
            {
                _playerCount = value;
                _playersText.text = value + "/" + MaxPlayers;
            }
        }
        
        [UxmlAttribute]
        public int MaxPlayers 
        {
            get => _maxPlayers;
            set
            {
                _maxPlayers = value;
                _playersText.text = PlayerCount + "/" + value;
            }
        }
        
        public LobbyButton()
        {
            AddToClassList(ClassName);
            
            _nameText = new Label();
            _nameText.AddToClassList(NameTextClassName);
            Add(_nameText);
            
            _playersText = new Label();
            _playersText.AddToClassList(PlayersTextClassName);
            Add(_playersText);
        }
    }
}
