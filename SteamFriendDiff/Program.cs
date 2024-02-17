using Spectre.Console;
using Steam.Models.SteamCommunity;
using SteamWebAPI2.Interfaces;
using SteamWebAPI2.Utilities;
using Config;

namespace SteamFriendDiff;

public static class SteamFriendDiff
{
    private enum MenuItem
    {
        None,
        RecordFriends,
        CheckDiff,
        Quit
    }

    private struct FriendInstance
    {
        public readonly ulong ID;
        public readonly DateTime FriendsSince;

        public FriendInstance(ulong id, DateTime friendsSince)
        {
            this.ID = id;
            this.FriendsSince = friendsSince;
        }
    }

    private struct FavouriteUser
    {
        public string Name;
        public ulong ID;

        public FavouriteUser(string name, ulong id)
        {
            Name = name;
            ID = id;
        }
    }

    private struct UserInputResult
    {
        public ulong ID;
        public IReadOnlyCollection<FriendModel>? Friends;

        public UserInputResult(ulong id, IReadOnlyCollection<FriendModel>? friends)
        {
            ID = id;
            Friends = friends;
        }
    }

    private struct UserValidResult
    {
        public bool Valid;
        public IReadOnlyCollection<FriendModel>? Friends;
        
        public UserValidResult(bool valid, IReadOnlyCollection<FriendModel>? friends)
        {
            Valid = valid;
            Friends = friends;
        }
    }

    [ConfigValue("SteamKey", "Config.toml", "Steam API key. Get one at https://steamcommunity.com/dev/apikey")]
    private static string _steamKey = "";

    [ConfigValue("SteamKeyTestID", "Config.toml", "Steam Profile ID used to test that the API key is valid")]
    private static ulong _steamKeyTestID = 76561197960435530;

    [ConfigValue("FavouriteUsers", "Config.toml", "List of users that are shown in the UI ")]
    private static List<FavouriteUser> _favouriteUsers;

    public static async Task Main(string[] args)
    {
        ConfigManager.InitConfig(".");

        // Init steam API
        SteamUser? steamInterface;

        while (true)
        {
            if (string.IsNullOrWhiteSpace(_steamKey))
                _steamKey = AnsiConsole.Ask<string>(
                    "Input a Steam Web API key (get one at https://steamcommunity.com/dev/apikey): ");

            var webInterfaceFactory = new SteamWebInterfaceFactory(_steamKey);
            steamInterface = webInterfaceFactory.CreateSteamWebInterface<SteamUser>(new HttpClient());
            try
            {
                await steamInterface.GetPlayerSummaryAsync(_steamKeyTestID);
            }
            catch
            {
                AnsiConsole.Foreground = Color.Red;
                AnsiConsole.WriteLine("Steam API key appears to be invalid");
                AnsiConsole.ResetColors();
                _steamKey = "";
                continue;
            }

            ConfigManager.RefreshConfigValues();
            break;
        }

        // Runtime loop
        MenuItem selectedItem = MenuItem.None;
        while (selectedItem != MenuItem.Quit)
        {
            selectedItem = Menu();
            switch (selectedItem)
            {
                case MenuItem.RecordFriends:
                    await RecordFriends(steamInterface);
                    break;
                case MenuItem.CheckDiff:
                    await CheckDiff(steamInterface);
                    break;
                case MenuItem.None:
                case MenuItem.Quit:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(selectedItem), "Selected item was out of range!");
            }
        }
    }

    private static void PrintError(string message)
    {
        AnsiConsole.Foreground = Color.Red;
        AnsiConsole.WriteLine(message);
        AnsiConsole.ResetColors();
        Console.ReadLine();
    }

    private static MenuItem Menu()
    {
        AnsiConsole.Clear();
        var result = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Steam Friends Diff Tool")
            .AddChoices(new[]
                {
                    "Record Friends",
                    "Check Diff",
                    "Quit"
                }
            ));

        return result switch
        {
            "Record Friends" => MenuItem.RecordFriends,
            "Check Diff" => MenuItem.CheckDiff,
            "Quit" => MenuItem.Quit,
            _ => MenuItem.None
        };
    }

    private static async Task RecordFriends(ISteamUser steam, ulong steamId = 0)
    {
        if (steamId == 0)
        {
            var input = await AskForSteamID(steam);
            steamId = input.ID;
        }
        
        var friendsListData = friendsListResponse.Data;

        AnsiConsole.WriteLine($"{playerSummaryData.Nickname} has {friendsListData.Count} friends!");
        AnsiConsole.WriteLine("Writing friends list to file...");
        Directory.CreateDirectory($"RecordedLists/{playerSummaryData.SteamId}/");
        await File.WriteAllLinesAsync(
            $"RecordedLists/{playerSummaryData.SteamId}/{DateTime.Now.ToString("O").Replace(':', '_')}.sfl",
            friendsListData.Select(model => $"{model.SteamId};{model.FriendSince:O}"));
        AnsiConsole.WriteLine("Done!");
        Console.ReadLine();
    }

    private static async Task<List<FriendInstance>> GetFriendsFromSteamID(ISteamUser steam, ulong id)
    {
        List<FriendInstance> friends = new();
        var playerSummaryResponse = await steam.GetPlayerSummaryAsync(id);
        if (playerSummaryResponse is null || playerSummaryResponse.Data is null)
        {
            PrintError($"Failed to find Steam user with id {id}!");
            return friends;
        }

        var playerSummaryData = playerSummaryResponse.Data;

        if (playerSummaryData.ProfileVisibility is not ProfileVisibility.Public)
        {
            PrintError($"Steam user {playerSummaryData.Nickname} has a private profile! It must be public!");
            return friends;
        }

        ISteamWebResponse<IReadOnlyCollection<FriendModel>>? friendsListResponse;
        try
        {
            friendsListResponse = await steam.GetFriendsListAsync(id);
        }
        catch (Exception)
        {
            PrintError(
                $"Steam user {playerSummaryData.Nickname} likely has a private friends list! It must be public!");
            return friends;
        }

        if (friendsListResponse is null || friendsListResponse.Data is null)
        {
            PrintError($"Failed to get friends list of Steam user {playerSummaryData.Nickname} (with id: {id})!");
            return friends;
        }

        var friendsListData = friendsListResponse.Data;

        foreach (var friend in friendsListData)
            friends.Add(new FriendInstance(friend.SteamId, friend.FriendSince));

        return friends;
    }

    private static List<FriendInstance> GetFriendsFromSFLFile(string filepath)
    {
        var rawList = File.ReadAllLines(filepath);
        List<FriendInstance> listData = new();
        foreach (string element in rawList)
        {
            try
            {
                var parts = element.Split(';');
                listData.Add(new FriendInstance(ulong.Parse(parts[0]), DateTime.Parse(parts[1])));
            }
            catch (Exception)
            {
                AnsiConsole.Foreground = Color.Red;
                AnsiConsole.WriteLine($"Failed to parse line: {element}");
                AnsiConsole.ResetColors();
            }
        }

        return listData;
    }

    private static async Task CheckDiff(ISteamUser steamInterface)
    {
        var steamId = AnsiConsole.Ask<ulong>("Steam ID (64bit numeric): ");
        var dirName = $"RecordedLists/{steamId}";

        if (!Directory.Exists(dirName))
        {
            var result = AnsiConsole.Prompt(new SelectionPrompt<bool>()
                .Title("This steam id does not have any recorded lists. Would you like to record one now?")
                .AddChoices(new[] { true, false })
                .UseConverter(b => b ? "Yes" : "No"));
            if (result)
                await RecordFriends(steamInterface, steamId);
            return;
        }

        var files = Directory.GetFiles(dirName);
        Dictionary<DateTime, string> records = new();
        foreach (var file in files)
        {
            var filename = Path.GetFileNameWithoutExtension(file);
            bool validFilename = DateTime.TryParse(filename.Replace('_', ':'), out DateTime time);
            if (validFilename)
                records.Add(time, file);
        }

        if (records.Count <= 0)
        {
            var result = AnsiConsole.Prompt(new SelectionPrompt<bool>()
                .Title("This steam id does not have any recorded lists. Would you like to record one now?")
                .AddChoices(new[] { true, false })
                .UseConverter(b => b ? "Yes" : "No"));
            if (result)
                await RecordFriends(steamInterface, steamId);
            return;
        }

        var chosenRecord = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Choose a record to compare against")
            .AddChoices(records.Values.Prepend("Latest"))
            .UseConverter(value =>
                value == "Latest"
                    ? "Latest"
                    : DateTime.Parse(Path.GetFileNameWithoutExtension(value).Replace('_', ':')).ToString("f")));

        // Resolve chosen list filename from input
        string chosenList;
        if (chosenRecord == "Latest")
        {
            var latest = records.Keys.Max();
            chosenList = records[latest];
        }
        else
            chosenList = chosenRecord;

        // Load lists
        Dictionary<ulong, DateTime> prevFriends =
            GetFriendsFromSFLFile(chosenList).ToDictionary(i => i.ID, i => i.FriendsSince);
        var currentFriendsRaw = await GetFriendsFromSteamID(steamInterface, steamId);
        Dictionary<ulong, DateTime> currentFriends = currentFriendsRaw.ToDictionary(i => i.ID, i => i.FriendsSince);

        // Compare them
        int removed = 0, added = 0;

        AnsiConsole.Foreground = Color.Red;
        foreach (var prevFriend in prevFriends)
        {
            if (!currentFriends.ContainsKey(prevFriend.Key))
            {
                removed++;
                var playerSummaryResponse = await steamInterface.GetPlayerSummaryAsync(prevFriend.Key);
                if (playerSummaryResponse is null || playerSummaryResponse.Data is null)
                {
                    AnsiConsole.WriteLine($"- {prevFriend.Key} (Couldn't get username)");
                }
                else
                {
                    var playerSummaryData = playerSummaryResponse.Data;
                    AnsiConsole.WriteLine($"- {playerSummaryData.Nickname} ({prevFriend.Key})");
                }

                AnsiConsole.WriteLine($"After being friends since {prevFriend.Value:f} ({(DateTime.Now - prevFriend.Value).ToReadableString()})");
            }
        }

        AnsiConsole.ResetColors();

        AnsiConsole.Foreground = Color.Green;
        foreach (var currentFriend in currentFriends)
        {
            if (!prevFriends.ContainsKey(currentFriend.Key))
            {
                added++;
                var playerSummaryResponse = await steamInterface.GetPlayerSummaryAsync(currentFriend.Key);
                if (playerSummaryResponse is null || playerSummaryResponse.Data is null)
                {
                    AnsiConsole.WriteLine($"+ {currentFriend.Key} (Couldn't get username)");
                }
                else
                {
                    var playerSummaryData = playerSummaryResponse.Data;
                    AnsiConsole.WriteLine($"+ {playerSummaryData.Nickname} ({currentFriend.Key})");
                }

                AnsiConsole.WriteLine($"Since {currentFriend.Value:f}");
            }
        }

        AnsiConsole.ResetColors();

        AnsiConsole.WriteLine($"In total: {added} added, {removed} removed");
        AnsiConsole.WriteLine("Press enter to return to the menu");

        Console.ReadLine();
    }

    private static async Task<UserInputResult> AskForSteamID(ISteamUser steam, ulong providedId = 0)
    {
        string result = "";
        
        if (_favouriteUsers.Count != 0)
        {
            var prompt = new SelectionPrompt<string>().Title("Select a favourite user, or enter a Steam ID:");
            prompt.AddChoice("Enter Steam ID");
            prompt.AddChoices(_favouriteUsers.Select(user => user.Name));
            result = AnsiConsole.Prompt<string>(prompt);
        }

        IReadOnlyCollection<FriendModel>? friends = null;
        ulong id = providedId;

        if ((result != "Enter Steam ID" && _favouriteUsers.Count != 0) || id != 0)
        {
            FavouriteUser selected = new(); 
            if (id == 0)
            {
                selected = _favouriteUsers.Where(user => user.Name == result).ToList()[0];
                id = selected.ID; // Get the ID of the user with this name. Should never fail
            }
            
            var isValid = await IsUserValid(steam, id);
            if (!(isValid.Valid))
            {
                if (id == 0)
                {
                    PrintError($"Removing {selected.Name} as a favourite since their ID is invalid.");
                    _favouriteUsers.Remove(selected);
                }
                ConfigManager.RefreshConfigValues();
                id = 0;
            }
            else friends = isValid.Friends;
        }
        
        if (id == 0)
        {
            bool valid = false;
            while (!valid)
            {
                id = AnsiConsole.Ask<ulong>("Enter a Steam ID:");
                var validResult = await IsUserValid(steam, id);
                friends = validResult.Friends;
                valid = validResult.Valid;
            }

            bool addToFavs = AnsiConsole.Prompt(new ConfirmationPrompt("Add this user to your favourites?"));
            if (addToFavs)
            {
                string name = AnsiConsole.Ask<string>("Enter their name in your favourites: ");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _favouriteUsers.Add(new FavouriteUser(name, id));
                    ConfigManager.RefreshConfigValues();
                }
            }
        }

        return new UserInputResult(id, friends);
    }
    
    private static async Task<UserValidResult> IsUserValid(ISteamUser steam, ulong steamId)
    {
        var playerSummaryResponse = await steam.GetPlayerSummaryAsync(steamId);
        if (playerSummaryResponse is null || playerSummaryResponse.Data is null)
        {
            PrintError($"Failed to find Steam user with id {steamId}!");
        }

        var playerSummaryData = playerSummaryResponse?.Data;
        if (playerSummaryData is null or { ProfileVisibility: not ProfileVisibility.Public })
        {
            // Technically could be null here but that would be very strange so we won't handle it properly (won't crash though)
            PrintError($"Steam user {playerSummaryData?.Nickname} has a private profile! It must be public!");
            return new UserValidResult(false, null);
        }

        ISteamWebResponse<IReadOnlyCollection<FriendModel>>? friendsListResponse;
        try
        {
            friendsListResponse = await steam.GetFriendsListAsync(steamId);
        }
        catch (Exception)
        {
            PrintError(
                $"Steam user {playerSummaryData.Nickname} likely has a private friends list! It must be public!");
            return new UserValidResult(false, null);
        }

        if (friendsListResponse is null || friendsListResponse.Data is null)
        {
            PrintError($"Failed to get friends list of Steam user {playerSummaryData.Nickname} (with id: {steamId})!");
            return new UserValidResult(false, null);
        }

        return new UserValidResult(true, friendsListResponse.Data);
    }
    
    // Thanks to https://stackoverflow.com/a/4423615
    public static string ToReadableString(this TimeSpan span)
    {
        string formatted =
            $"{(span.Duration().Days > 0 ? $"{span.Days:0} day{(span.Days == 1 ? string.Empty : "s")}, " : string.Empty)}{(span.Duration().Hours > 0 ? $"{span.Hours:0} hour{(span.Hours == 1 ? string.Empty : "s")}, " : string.Empty)}{(span.Duration().Minutes > 0 ? $"{span.Minutes:0} minute{(span.Minutes == 1 ? string.Empty : "s")}, " : string.Empty)}{(span.Duration().Seconds > 0 ? $"{span.Seconds:0} second{(span.Seconds == 1 ? string.Empty : "s")}" : string.Empty)}";

        if (formatted.EndsWith(", ")) formatted = formatted.Substring(0, formatted.Length - 2);

        if (string.IsNullOrEmpty(formatted)) formatted = "0 seconds";

        return formatted;
    }
}
