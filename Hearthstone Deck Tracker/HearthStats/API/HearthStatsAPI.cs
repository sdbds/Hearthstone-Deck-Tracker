﻿#region

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Stats;
using Newtonsoft.Json;

#endregion

namespace Hearthstone_Deck_Tracker.HearthStats.API
{
	public class HearthStatsAPI
	{
		private const string BaseUrl = "http://192.237.249.9";
		private static string _authToken;

		private static readonly List<GameMode> ValidGameModes = new List<GameMode>
		{
			GameMode.Arena,
			GameMode.Casual,
			GameMode.Ranked,
			GameMode.Friendly
		};

		public static bool IsLoggedIn
		{
			get { return !string.IsNullOrEmpty(_authToken); }
		}

		public static bool Logout()
		{
			_authToken = "";
			try
			{
				if(File.Exists(Config.Instance.HearthStatsFilePath))
					File.Delete(Config.Instance.HearthStatsFilePath);
				return true;
			}
			catch(Exception ex)
			{
				Logger.WriteLine("Error deleting hearthstats credentials file\n" + ex);
				return false;
			}
		}

		public static bool LoadCredentials()
		{
			if(File.Exists(Config.Instance.HearthStatsFilePath))
			{
				try
				{
					using(var reader = new StreamReader(Config.Instance.HearthStatsFilePath))
					{
						dynamic content = JsonConvert.DeserializeObject(reader.ReadToEnd());
						_authToken = content.auth_token;
					}
					return true;
				}
				catch(Exception e)
				{
					Logger.WriteLine("Error loading credentials\n" + e);
					return false;
				}
			}
			return false;
		}

		public static bool IsValidGame(GameStats game)
		{
			var baseMsg = "Game " + game + " is not valid ({0})";
			if(game == null)
			{
				Logger.WriteLine(string.Format(baseMsg, "null"), "HearthStatsAPI");
				return false;
			}
			if(game.IsClone)
			{
				Logger.WriteLine(string.Format(baseMsg, "IsClone"), "HearthStatsAPI");
				return false;
			}
			if(ValidGameModes.All(mode => game.GameMode != mode))
			{
				Logger.WriteLine(string.Format(baseMsg, "invalid game mode: " + game.GameMode), "HearthStatsAPI");
				return false;
			}
			if(game.Result == GameResult.None)
			{
				Logger.WriteLine(string.Format(baseMsg, "invalid result: none"), "HearthStatsAPI");
				return false;
			}
			if(game.HasHearthStatsId)
			{
				Logger.WriteLine(string.Format(baseMsg, "already submitted"), "HearthStatsAPI");
				return false;
			}
			return true;
		}

		private static HttpWebRequest CreateRequest(string url, string method)
		{
			var request = (HttpWebRequest)WebRequest.Create(url);
			request.ContentType = "application/json";
			request.Accept = "application/json";
			request.Method = method;
			return request;
		}

		#region Async

		public static async Task<LoginResult> LoginAsync(string email, string pwd)
		{
			try
			{
				const string url = BaseUrl + "/api/v2/users/sign_in";
				var data = JsonConvert.SerializeObject(new {user_login = new {email, password = pwd}});
				var json = await PostAsync(url, data);
				dynamic response = JsonConvert.DeserializeObject(json);
				if((bool)response.success)
				{
					_authToken = response.auth_token;
					if(Config.Instance.RememberHearthStatsLogin)
					{
						using(var writer = new StreamWriter(Config.Instance.HearthStatsFilePath, false))
							writer.Write(JsonConvert.SerializeObject(new {auth_token = _authToken}));
					}
					return new LoginResult(true);
				}
				return new LoginResult(false, response.ToString());
			}
			catch(Exception e)
			{
				Console.WriteLine(e);
				return new LoginResult(false, e.Message);
			}
		}

		public static async Task<List<Deck>> GetDecksAsync(long unixTime)
		{
			Logger.WriteLine("getting decks since " + unixTime, "HearthStatsAPI");
			var url = BaseUrl + "/api/v2/decks/hdt_after?auth_token=" + _authToken;
			var data = JsonConvert.SerializeObject(new {date = unixTime});
			Console.WriteLine(data);
			try
			{
				var response = await PostAsync(url, data);
				dynamic json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					var decks = (DeckObject[])json.data;
					return decks.Select(d => d.ToDeck()).ToList(); //todo ToDecks()
				}
				return null;
				//success?
			}
			catch(Exception e)
			{
				Console.WriteLine(e);
				return null;
			}
		}

		public static async Task<PostResult> PostDeckAsync(Deck deck)
		{
			if(deck == null)
			{
				Logger.WriteLine("deck is null", "HearthStatsAPI");
				return PostResult.Failed;
			}
			if(deck.HasHearthStatsId)
			{
				Logger.WriteLine("deck already posted", "HearthStatsAPI");
				return PostResult.Failed;
			}
			Logger.WriteLine("uploading deck: " + deck, "HearthStatsAPI");
			var url = BaseUrl + "/api/v2/decks/hdt_create?auth_token=" + _authToken;
			var cards = deck.Cards.Select(x => new CardObject(x));
			var data = JsonConvert.SerializeObject(new
			{
				name = deck.Name,
				note = deck.Note,
				tags = deck.Tags,
				@class = deck.Class,
				cards,
				// url,
				// version = deck.Version.ToString("{M}.{m}")
			});
			try
			{
				var response = await PostAsync(url, data);
				Console.WriteLine(response);
				dynamic json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					deck.VersionOnHearthStats = true;
					deck.HearthStatsId = json.data.id;
					Logger.WriteLine("assigned id to deck: " + deck.HearthStatsId, "HearthStatsAPI");
					return PostResult.WasSuccess;
				}
				return PostResult.CanRetry;
			}
			catch(Exception e)
			{
				Logger.WriteLine(e.ToString(), "HearthStatsAPI");
				return PostResult.CanRetry;
			}
		}

		public static async Task<PostResult> PostVersionAsync(Deck deck, string hearthStatsId)
		{
			if(deck == null)
			{
				Logger.WriteLine("version(deck) is null", "HearthStatsAPI");
				return PostResult.Failed;
			}
			if(deck.VersionOnHearthStats)
			{
				Logger.WriteLine("version(deck) already posted", "HearthStatsAPI");
				return PostResult.Failed;
			}
			var version = deck.Version.ToString("{M}.{m}");
			Logger.WriteLine("uploading version " + version + " of " + deck, "HearthStatsAPI");
			var url = BaseUrl + "/api/v2/decks/create_version?auth_token=" + _authToken;
			var cards = deck.Cards.Select(x => new CardObject(x));
			var data = JsonConvert.SerializeObject(new {deck_id = deck.DeckId, version, cards});
			try
			{
				var response = await PostAsync(url, data);
				Console.WriteLine(response);
				dynamic json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					deck.VersionOnHearthStats = true;
					deck.HearthStatsId = hearthStatsId;
					Logger.WriteLine("assigned id to version: " + deck.HearthStatsId, "HearthStatsAPI");
					return PostResult.WasSuccess;
				}
				return PostResult.CanRetry;
			}
			catch(Exception e)
			{
				Logger.WriteLine(e.ToString(), "HearthStatsAPI");
				return PostResult.CanRetry;
			}
		}


		public static async Task<PostResult> PostGameResultAsync(GameStats game, Deck deck)
		{
			if(!IsValidGame(game))
				return PostResult.Failed;
			if(!deck.HasHearthStatsId)
			{
				Logger.WriteLine("can not upload game, deck has no hearthstats id", "HearthStatsAPI");
				return PostResult.Failed;
			}
			Logger.WriteLine("uploading match: " + game, "HearthStatsAPI");
			var url = BaseUrl + "/api/v2/matches/hdt_new?auth_token=" + _authToken;

			dynamic gameObj = new ExpandoObject();
			gameObj.mode = game.GameMode.ToString();
			gameObj.@class = string.IsNullOrEmpty(game.PlayerHero) ? deck.Class : game.PlayerHero;
			gameObj.result = game.Result.ToString();
			gameObj.coin = game.Coin.ToString().ToLower();
			gameObj.numturns = game.Turns;
			gameObj.duration = (int)(game.EndTime - game.StartTime).TotalSeconds;
			gameObj.deck_id = deck.HearthStatsId;
			if(!string.IsNullOrEmpty(game.OpponentHero))
				gameObj.oppclass = game.OpponentHero;
			if(!string.IsNullOrEmpty(game.OpponentName))
				gameObj.oppname = game.OpponentName;
			if(!string.IsNullOrEmpty(game.Note))
				gameObj.notes = game.Note;
			if(game.GameMode == GameMode.Ranked && game.HasRank)
				gameObj.ranklvl = game.Rank.ToString();

			var data = JsonConvert.SerializeObject(gameObj);

			try
			{
				var response = await PostAsync(url, data);
				var json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					game.HearthStatsId = json.data.id;
					Logger.WriteLine("assigned id to version: " + deck.HearthStatsId, "HearthStatsAPI");
					return PostResult.WasSuccess;
				}
				return PostResult.CanRetry;
			}
			catch(Exception e)
			{
				Logger.WriteLine(e.ToString(), "HearthStatsAPI");
				return PostResult.CanRetry;
			}
		}

		private static async Task<string> PostAsync(string url, string data)
		{
			return await PostAsync(url, Encoding.UTF8.GetBytes(data));
		}

		private static async Task<string> PostAsync(string url, byte[] data)
		{
			var request = CreateRequest(url, "POST");
			using(var stream = await request.GetRequestStreamAsync())
				stream.Write(data, 0, data.Length);
			var response = await request.GetResponseAsync();
			using(var responseStream = response.GetResponseStream())
			using(var reader = new StreamReader(responseStream))
				return reader.ReadToEnd();
		}

		private static async Task<string> GetAsync(string url)
		{
			var request = CreateRequest(url, "GET");
			var response = await request.GetResponseAsync();
			using(var responseStream = response.GetResponseStream())
			using(var reader = new StreamReader(responseStream))
				return reader.ReadToEnd();
		}

		public static async Task<string> GetGamesAsync(long unixTime)
		{
			return "";
		}

		public static async Task<PostResult> DeleteDeckAsync(Deck deck)
		{
			if(deck == null)
			{
				Logger.WriteLine("error: deck is null", "HearthStatsAPI");
				return PostResult.Failed;
			}
			if(!deck.HasHearthStatsId)
			{
				Logger.WriteLine("error: deck has no HearthStatsId", "HearthStatsAPI");
				return PostResult.Failed;
			}
			Logger.WriteLine("deleting deck: " + deck, "HearthStatsAPI");

			var url = BaseUrl + "@@@@@@@@@@@@@@@@" + _authToken; // TODO 
			var data = JsonConvert.SerializeObject(new {deck_id = deck.HearthStatsId});
			try
			{
				var response = await PostAsync(url, data);
				Console.WriteLine(response);
				dynamic json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					Logger.WriteLine("deleted deck", "HearthStatsAPI");
					return PostResult.WasSuccess;
				}
				Logger.WriteLine("error: " + response, "HearthStatsAPI");
				return PostResult.CanRetry;
			}
			catch(Exception e)
			{
				Logger.WriteLine(e.ToString(), "HearthStatsAPI");
				return PostResult.CanRetry;
			}
		}

		public static async Task<PostResult> DeleteMatchAsync(GameStats game)
		{
			if(game == null)
			{
				Logger.WriteLine("error: game is null", "HearthStatsAPI");
				return PostResult.Failed;
			}
			if(!game.HasHearthStatsId)
			{
				Logger.WriteLine("error: game has no HearthStatsId", "HearthStatsAPI");
				return PostResult.Failed;
			}
			Logger.WriteLine("deleting game: " + game, "HearthStatsAPI");

			var url = BaseUrl + "@@@@@@@@@@@@@@@@" + _authToken; // TODO
			var data = JsonConvert.SerializeObject(new {deck_id = game.HearthStatsId});
			try
			{
				var response = await PostAsync(url, data);
				Console.WriteLine(response);
				dynamic json = JsonConvert.DeserializeObject(response);
				if(json.status.ToString() == "success")
				{
					Logger.WriteLine("deleted game", "HearthStatsAPI");
					return PostResult.WasSuccess;
				}
				Logger.WriteLine("error: " + response, "HearthStatsAPI");
				return PostResult.CanRetry;
			}
			catch(Exception e)
			{
				Logger.WriteLine(e.ToString(), "HearthStatsAPI");
				return PostResult.CanRetry;
			}
		}

		private class CardObject
		{
			public readonly int count;
			public readonly string id;

			public CardObject(Card card)
			{
				id = card.Id;
				count = card.Count;
			}

			public Card ToCard()
			{
				var card = Game.GetCardFromId(id);
				card.Count = count;
				return card;
			}
		}

		private class DeckObject
		{
			public CardObject[] cards;
			public string @class;
			//public string url;
			//public string version;
			public string id;
			public string name;
			public string notes;
			public string[] tags;

			public Deck ToDeck()
			{
				return new Deck();
				//return new Deck(name, @class, cards.Select(x => x.ToCard()), tags, notes, url, DateTime.Now, new List<Card>(),
				//         SerializableVersion.Parse(version), new List<Deck>(), true, id, Guid.Empty);
			}
		}

		#endregion

		#region Sync

		public static LoginResult Login(string email, string pwd)
		{
			return LoginAsync(email, pwd).Result;
		}

		public static List<Deck> GetDecks(long unixTime)
		{
			return GetDecksAsync(unixTime).Result;
		}

		public static PostResult PostDeck(Deck deck)
		{
			return PostDeckAsync(deck).Result;
		}

		public static PostResult PostGameResult(GameStats game, Deck deck)
		{
			return PostGameResultAsync(game, deck).Result;
		}

		private static string Post(string url, string data)
		{
			return PostAsync(url, data).Result;
		}

		private static string Post(string url, byte[] data)
		{
			return PostAsync(url, data).Result;
		}

		private static string Get(string url)
		{
			return GetAsync(url).Result;
		}

		public static string GetGames(long unixTime)
		{
			return GetGamesAsync(unixTime).Result;
		}

		public static PostResult DeleteDeck(Deck deck)
		{
			return DeleteDeckAsync(deck).Result;
		}

		public static PostResult PostVersion(Deck version, string hearthStatsId)
		{
			return PostVersionAsync(version, hearthStatsId).Result;
		}

		#endregion
	}
}