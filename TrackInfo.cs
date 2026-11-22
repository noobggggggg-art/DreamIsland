namespace DreamIsland
{
    /// <summary>Строка вкладки «Статистика»: одна отслеживаемая программа и её время.</summary>
    public sealed class TrackInfo
    {
        public string Id, Name;
        public bool Enabled, Builtin, Running;
        public double Session, Today, Week;   // секунды
    }
}
