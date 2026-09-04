namespace StashUtility.Models
{
    public class JewelMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } // "Prefix" or "Suffix"
        public string Category { get; set; } // "Ruby", "Emerald", "Sapphire", "Diamond", "Time-Lost", etc.

        public string GameModId { get; set; }
        public float MinRoll { get; set; } = 0f;
        public float MaxRoll { get; set; } = 0f;

        public JewelMod(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
