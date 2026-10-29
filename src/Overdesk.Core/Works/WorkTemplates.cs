namespace Overdesk.Core.Works;

/// <summary>A starting point for a work: a block layout plus look (color, icon, fonts).</summary>
public sealed record WorkTemplate(
    string Id, string Name, string Description, string Category, string Icon, string Color,
    string? TitleFont, string? BodyFont, string DefaultTitle, Func<List<WorkBlock>> Blocks)
{
    public Work Create() => new()
    {
        Title = DefaultTitle, Template = Id, Color = Color, Icon = Icon,
        TitleFont = TitleFont, BodyFont = BodyFont, Blocks = Blocks(),
    };
}

/// <summary>The built-in template gallery.</summary>
public static class WorkTemplates
{
    public const string Writing = "Writing";
    public const string Lists = "Lists & planning";
    public const string Games = "Game making";
    public const string Life = "Everyday";

    private static WorkBlock H(string text, int level = 2) => new() { Kind = BlockKind.Heading, Text = text, Level = level };
    private static WorkBlock T(string text = "") => new() { Kind = BlockKind.Text, Text = text };
    private static WorkBlock Do(string text = "", bool done = false) => new() { Kind = BlockKind.Todo, Text = text, Checked = done };
    private static WorkBlock Flag(string name, bool on = false) => new() { Kind = BlockKind.Toggle, Text = name, Checked = on };
    private static WorkBlock Fld(string name, string value = "") => new() { Kind = BlockKind.Field, Text = name, Value = value };
    private static WorkBlock Num(string name, double n = 0) => new() { Kind = BlockKind.Number, Text = name, Number = n };
    private static WorkBlock B(string text = "") => new() { Kind = BlockKind.Bullet, Text = text };
    private static WorkBlock Q(string text = "") => new() { Kind = BlockKind.Quote, Text = text };
    private static WorkBlock Hr() => new() { Kind = BlockKind.Divider };

    public static readonly IReadOnlyList<WorkTemplate> All = new WorkTemplate[]
    {
        new("blank", "Blank page", "An empty page. Add whatever blocks you need.", Writing, "IconNote", "Ultraviolet",
            null, null, "Untitled", () => new() { T() }),

        new("note", "Quick note", "A title, a few lines, done.", Life, "IconNote", "Cyber",
            null, null, "Quick note", () => new() { T("Write here…") }),

        new("story", "Story", "Premise, characters and chapters for a tale.", Writing, "IconBook", "Sakura",
            "Cormorant Garamond", "Lora", "Untitled story", () => new()
            {
                Fld("Genre"), Fld("Setting"), Fld("Point of view", "Third person"),
                Q("The one-sentence premise goes here."),
                H("Characters"), B("Protagonist: "), B("Antagonist: "), B(""),
                Hr(),
                H("Chapter 1", 1), T("Once upon a time…"),
            }),

        new("chapter", "Chapter draft", "Just writing space, with a word of focus.", Writing, "IconBook", "Amber",
            "Playfair Display", "EB Garamond", "Chapter", () => new()
            {
                H("Chapter", 1), Fld("Goal of this chapter"), Hr(), T(),
            }),

        new("dialogue", "Dialogue script", "Lines per speaker for cutscenes and conversations.", Writing, "IconSend", "Cyber",
            "Special Elite", "Special Elite", "Dialogue", () => new()
            {
                Fld("Scene"), Fld("Location"), Hr(),
                Fld("Speaker 1", "…"), Fld("Speaker 2", "…"), Fld("Speaker 1", "…"),
                Q("(stage direction)"),
            }),

        new("todo", "To-do list", "Check things off. Progress shows on the card.", Lists, "IconCheck", "Toxic",
            "Poppins", "Poppins", "To-do", () => new() { Do("First thing"), Do("Second thing"), Do("") }),

        new("project", "Project board", "Backlog, in progress and done in one page.", Lists, "IconTabs", "Ultraviolet",
            "Space Grotesk", "Poppins", "Project", () => new()
            {
                Fld("Goal"), Fld("Deadline"),
                H("Backlog"), Do("Idea one"), Do("Idea two"),
                H("In progress"), Do("Current task"),
                H("Done"), Do("Project created", true),
            }),

        new("roadmap", "Roadmap", "Milestones with a status flag each.", Lists, "IconRocket", "Crimson",
            "Oswald", "Poppins", "Roadmap", () => new()
            {
                H("Milestone 1: Prototype"), Flag("Reached"), Do("Core loop"), Do("Placeholder art"),
                H("Milestone 2: Vertical slice"), Flag("Reached"), Do("One polished level"),
                H("Milestone 3: Release"), Flag("Reached"), Do("Store page"), Do("Trailer"),
            }),

        new("ideas", "Idea dump", "A messy list of ideas to sort later.", Life, "IconSparkle", "Amber",
            "Caveat", "Caveat", "Ideas", () => new() { B("An idea"), B(""), B("") }),

        new("journal", "Daily journal", "Date, mood and what happened.", Life, "IconBook", "Sakura",
            "Dancing Script", "Lora", "Today", () => new()
            {
                Fld("Date", DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)),
                Num("Mood (1-10)", 7), H("Highlights"), B(), B(), H("Thoughts"), T(),
            }),

        new("meeting", "Meeting notes", "Who, what was decided, who does what.", Life, "IconPanel", "Cyber",
            "Space Grotesk", "Poppins", "Meeting", () => new()
            {
                Fld("Date"), Fld("People"), H("Notes"), T(), H("Decisions"), B(), H("Action items"), Do(), Do(),
            }),

        new("gdd", "Game design document", "Pitch, pillars, mechanics, art and scope.", Games, "IconRocket", "Crimson",
            "Orbitron", "Poppins", "Game design document", () => new()
            {
                Fld("Working title"), Fld("Genre"), Fld("Platforms"), Fld("Engine"),
                Q("Pitch: the game in one sentence."),
                H("Design pillars"), B("Pillar one"), B("Pillar two"), B("Pillar three"),
                H("Core loop"), T("What does the player do, minute to minute?"),
                H("Mechanics"), B(), B(),
                H("Story and world"), T(),
                H("Art and sound"), T(),
                H("Scope"), Do("Prototype"), Do("Vertical slice"), Do("Release"),
            }),

        new("character", "Character sheet", "Profile, stats and flags for a character.", Games, "IconPin", "Ultraviolet",
            "Cinzel", "Lora", "Character", () => new()
            {
                Fld("Name"), Fld("Role"), Fld("Age"),
                H("Stats"), Num("Health", 100), Num("Strength", 10), Num("Agility", 10), Num("Intelligence", 10),
                H("Flags"), Flag("Alive", true), Flag("Playable"), Flag("Hostile"),
                H("Traits"), B(), B(),
                H("Backstory"), T(),
            }),

        new("quest", "Quest", "Giver, steps and rewards, with state flags.", Games, "IconBook", "Amber",
            "MedievalSharp", "Lora", "Quest", () => new()
            {
                Fld("Quest giver"), Fld("Location"),
                Flag("Started"), Flag("Completed"), Flag("Optional"),
                Num("XP reward", 100), Num("Gold reward", 50),
                H("Steps"), Do("Talk to the giver"), Do("Do the thing"), Do("Return for the reward"),
                H("Story"), T(),
            }),

        new("variables", "Game variables", "True/false flags, numbers and strings your game remembers.", Games, "IconGauge", "Toxic",
            "JetBrains Mono", "JetBrains Mono", "Game state", () => new()
            {
                H("Flags (bool)"), Flag("hasKey"), Flag("metTheKing"), Flag("bridgeIsBroken"), Flag("isNight"),
                H("Numbers"), Num("gold", 0), Num("playerLevel", 1), Num("deaths", 0),
                H("Strings"), Fld("playerName"), Fld("currentLocation", "Village"),
            }),

        new("world", "World building", "Places, factions, history and rules.", Games, "IconGlobe", "Cyber",
            "Cinzel Decorative", "EB Garamond", "World", () => new()
            {
                Fld("World name"), Fld("Tone"),
                H("Geography"), T(), H("Factions"), B(), B(), H("History"), T(), H("Rules of magic / technology"), T(),
            }),

        new("bugs", "Bug tracker", "Bugs with a fixed flag each.", Games, "IconAlert", "Crimson",
            "Space Grotesk", "JetBrains Mono", "Bugs", () => new()
            {
                H("Open"), Do("Player falls through the floor in level 2"), Do(""),
                H("Fixed"), Do("Menu music loops twice", true),
                H("Notes"), T(),
            }),

        new("release", "Release checklist", "Everything to tick off before shipping.", Lists, "IconRocket", "Toxic",
            "Bebas Neue", "Poppins", "Release checklist", () => new()
            {
                Do("All tests pass"), Do("Version number updated"), Do("Changelog written"), Do("Build signed"),
                Do("Store page and screenshots ready"), Do("Announcement drafted"),
            }),

        new("shopping", "Shopping list", "Things to buy.", Life, "IconBox", "Amber",
            "Pacifico", "Poppins", "Shopping", () => new() { Do("Milk"), Do("Bread"), Do("") }),
    };

    public static WorkTemplate Find(string id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}
