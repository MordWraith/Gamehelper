namespace StashUtility.Models
{
    public class WaystoneMod
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int ItemRarity { get; set; }
        public int PackSize { get; set; }
        public int MonsterRarity { get; set; }
        public int MonsterEffectiveness { get; set; }
        public int WaystoneDropChance { get; set; }

        public WaystoneMod(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
