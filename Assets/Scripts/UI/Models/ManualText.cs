using System.Collections.Generic;
using Newtonsoft.Json;

namespace WAR2D.UI
{
    /// <summary>The words of one unit or building page.</summary>
    public sealed class ManualPageWords
    {
        [JsonProperty("role")] public string Role;
        [JsonProperty("body")] public List<string> Body = new List<string>();
    }

    /// <summary>One heading and its paragraphs on a rules page.</summary>
    public sealed class ManualRuleSection
    {
        [JsonProperty("heading")] public string Heading;
        [JsonProperty("paras")] public List<string> Paras = new List<string>();
    }

    /// <summary>The words of one rules page.</summary>
    public sealed class ManualRuleWords
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("title")] public string Title;
        [JsonProperty("lead")] public string Lead;
        [JsonProperty("diagram")] public string Diagram;
        [JsonProperty("sections")] public List<ManualRuleSection> Sections = new List<ManualRuleSection>();
    }

    /// <summary>One group of the Controls page: rows of label, keys and an optional "new" flag.</summary>
    public sealed class ManualControlGroup
    {
        [JsonProperty("group")] public string Group;
        [JsonProperty("rows")] public List<List<string>> Rows = new List<List<string>>();
    }

    /// <summary>The damage table page's words.</summary>
    public sealed class ManualDamageWords
    {
        [JsonProperty("lead")] public string Lead;
        [JsonProperty("notes")] public List<string> Notes = new List<string>();
    }

    /// <summary>Everything in the approved text file.</summary>
    public sealed class ManualWords
    {
        [JsonProperty("counterNote")] public string CounterNote;
        [JsonProperty("counterNoteBomber")] public string CounterNoteBomber;
        [JsonProperty("noFight")] public string NoFight;
        [JsonProperty("damageTable")] public ManualDamageWords DamageTable;
        [JsonProperty("units")] public Dictionary<string, ManualPageWords> Units;
        [JsonProperty("buildings")] public Dictionary<string, ManualPageWords> Buildings;
        [JsonProperty("rules")] public List<ManualRuleWords> Rules;
        [JsonProperty("controls")] public List<ManualControlGroup> Controls;
    }

    /// <summary>
    /// The Field manual's words (v0.18), exactly as approved on the mock website. They live in <c>tools/manual-text.json</c>,
    /// which <c>tools/manual-mock.js --write-text</c> copies into this class; a test fails if the two differ. No number is typed in
    /// the words: {placeholders} are filled from the config when the manual opens.
    /// </summary>
    public static partial class ManualText
    {
        private static ManualWords words;

        /// <summary>The text file's contents as compiled in.</summary>
        public static string Raw => Json;

        /// <summary>The words, parsed once.</summary>
        public static ManualWords Words => words ??= JsonConvert.DeserializeObject<ManualWords>(Json);
    }
}
