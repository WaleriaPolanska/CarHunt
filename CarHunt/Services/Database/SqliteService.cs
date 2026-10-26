using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CarHunt.Models;
using SQLite;

namespace CarHunt.Services.Database;

public class SqliteService
{
    private readonly SQLiteAsyncConnection _db;

    public SqliteService()
    {
        var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "carhunt.db");
        _db = new SQLiteAsyncConnection(dbPath);
        _db.CreateTableAsync<Car>().Wait();
        // Cleanup for favourites stored before stable keys existed: they all collided
        // into rows with an empty key. The count is checked first so the usual case —
        // nothing to clean — is a lookup on the DedupeKey index instead of a write
        // transaction taken on every launch, on the UI thread.
        var orphans = _db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM Car WHERE DedupeKey IS NULL OR DedupeKey = ''").Result;
        if (orphans > 0)
            _db.ExecuteAsync("DELETE FROM Car WHERE DedupeKey IS NULL OR DedupeKey = ''").Wait();
    }

    public Task<List<Car>> GetFavoritesAsync() =>
        _db.Table<Car>().Where(c => c.IsFavorite).ToListAsync();

    /// <summary>
    /// The dedupe keys that are currently favourited. Asks for that one column: this
    /// runs after every search, and it used to load whole rows — titles, descriptions,
    /// image URLs and all — only to read the key off each of them.
    /// </summary>
    public async Task<HashSet<string>> GetFavoriteKeysAsync()
    {
        var keys = await _db.QueryScalarsAsync<string>(
            "SELECT DedupeKey FROM Car WHERE IsFavorite = 1 AND DedupeKey <> ''");
        return new HashSet<string>(keys);
    }

    public async Task ToggleFavoriteAsync(Car car)
    {
        var key = BuildKey(car);
        car.DedupeKey = key;

        var existing = await _db.Table<Car>().Where(c => c.DedupeKey == key).FirstOrDefaultAsync();
        if (existing != null)
        {
            // Already a favourite → tapping the heart removes it.
            car.IsFavorite = false;
            await _db.DeleteAsync(existing);
        }
        else
        {
            // Not stored yet → add as favourite.
            car.IsFavorite = true;
            car.Id = 0; // let AUTOINCREMENT assign a fresh id
            await _db.InsertAsync(car);
        }
    }

    /// <summary>
    /// Builds a stable identity for a car. SourceUrl is preferred, but many scraped
    /// items have an empty URL, so we fall back to the meaningful fields. Without this,
    /// every car with an empty SourceUrl collided into a single favourite row.
    /// </summary>
    public static string BuildKey(Car c)
    {
        if (!string.IsNullOrWhiteSpace(c.SourceUrl))
            return c.SourceUrl.Trim();
        return string.Join("|",
            c.Source, c.Title, c.Year, c.Price, c.Mileage).ToLowerInvariant();
    }

    public Task SaveCarAsync(Car car) => _db.InsertOrReplaceAsync(car);
    public Task DeleteCarAsync(Car car) => _db.DeleteAsync(car);
}

