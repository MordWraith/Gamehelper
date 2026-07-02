namespace StashUtility.Models
{
    public class TabletMod
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public TabletMod(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }
}
