namespace StashUtility.Models
{
    public class TabletMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Affix { get; set; } = "prefix";

        public float MinRoll { get; set; } = 0f;
        public float MaxRoll { get; set; } = 0f;

        public TabletMod(string id, string name, string affix = "prefix")
        {
            Id = id;
            Name = name;
            Affix = affix;
        }
    }
}
