using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AchievementLabs.Core;

public sealed class SharedUnlockMethods
{
	public static readonly Uri PublishedUri = new Uri("https://raw.githubusercontent.com/Achievement-Labs/achievement-labs/main/catalog/achievement-unlock-methods.json");

	private Dictionary<string, bool> methods = new Dictionary<string, bool>();

	public int Count => methods.Count;

	public bool? Find(string title)
	{
		if (!methods.TryGetValue(title, out var value))
		{
			return null;
		}
		return value;
	}

	public static Dictionary<string, bool> Parse(string json)
	{
		if (Encoding.UTF8.GetByteCount(json) > 2097152)
		{
			throw new InvalidDataException("Support catalogue exceeds limit.");
		}
		using JsonDocument jsonDocument = JsonDocument.Parse(json);
		JsonElement rootElement = jsonDocument.RootElement;
		if (rootElement.GetProperty("schemaVersion").GetInt32() != 1 || rootElement.GetProperty("methods").ValueKind != JsonValueKind.Object)
		{
			throw new InvalidDataException("Invalid support catalogue.");
		}
		Dictionary<string, bool> dictionary = new Dictionary<string, bool>();
		foreach (JsonProperty item in rootElement.GetProperty("methods").EnumerateObject())
		{
            if (dictionary.Count >= 20000 || !uint.TryParse(item.Name, out var result) || result == 0 || item.Name != result.ToString()
                || item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !dictionary.TryAdd(item.Name, item.Value.GetBoolean()))
			{
				throw new InvalidDataException("Invalid confirmed game type.");
			}
		}
		return dictionary;
	}

	public static string Export(IReadOnlyDictionary<string, bool> confirmed)
	{
		string text = JsonSerializer.Serialize(new
		{
			schemaVersion = 1,
			methods = confirmed.OrderBy<KeyValuePair<string, bool>, string>((KeyValuePair<string, bool> x) => x.Key, StringComparer.Ordinal).ToDictionary((KeyValuePair<string, bool> x) => x.Key, (KeyValuePair<string, bool> x) => x.Value)
		});
		Parse(text);
		return text;
	}

	public static SharedUnlockMethods Bundled()
	{
		SharedUnlockMethods sharedUnlockMethods = new SharedUnlockMethods();
		using Stream? stream = typeof(SharedUnlockMethods).Assembly.GetManifestResourceStream("AchievementLabs.SharedUnlockMethods.json");
		if (stream != null)
		{
			using StreamReader streamReader = new StreamReader(stream);
			sharedUnlockMethods.methods = Parse(streamReader.ReadToEnd());
		}
		return sharedUnlockMethods;
	}

	public async Task RefreshAsync(HttpClient http, string cache, CancellationToken token)
	{
		try
		{
			if (File.Exists(cache) && new FileInfo(cache).Length <= 2097152)
			{
				methods = Parse(await File.ReadAllTextAsync(cache, token));
				if (File.GetLastWriteTimeUtc(cache) > DateTime.UtcNow.AddHours(-24.0))
				{
					return;
				}
			}
		}
		catch (Exception ex) when (((ex is IOException || ex is JsonException || ex is InvalidDataException || ex is KeyNotFoundException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException) ? 1 : 0) != 0)
		{
		}
		try
		{
			using HttpResponseMessage response = await http.GetAsync(PublishedUri, HttpCompletionOption.ResponseHeadersRead, token);
			response.EnsureSuccessStatusCode();
			if (response.Content.Headers.ContentLength > 2097152)
			{
				throw new InvalidDataException();
			}
			using Stream input = await response.Content.ReadAsStreamAsync(token);
			using MemoryStream data = new MemoryStream();
			byte[] bytes = new byte[16384];
			int num;
			while ((num = await input.ReadAsync(bytes, token)) > 0)
			{
				if (data.Length + num > 2097152)
				{
					throw new InvalidDataException();
				}
				data.Write(bytes, 0, num);
			}
			string @string = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(data.ToArray());
			Dictionary<string, bool> next = Parse(@string);
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cache))!);
			await File.WriteAllTextAsync(cache + ".tmp", @string, token);
			File.Move(cache + ".tmp", cache, overwrite: true);
			methods = next;
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex3) when (((ex3 is HttpRequestException || ex3 is OperationCanceledException || ex3 is IOException || ex3 is JsonException || ex3 is InvalidDataException || ex3 is KeyNotFoundException || ex3 is InvalidOperationException || ex3 is FormatException || ex3 is OverflowException || ex3 is DecoderFallbackException) ? 1 : 0) != 0)
		{
		}
	}
}
