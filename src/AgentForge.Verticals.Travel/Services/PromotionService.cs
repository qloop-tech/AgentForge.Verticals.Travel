using Microsoft.Extensions.Configuration;
using System.Text.Json;
using AgentForge.Verticals.Travel.Models;

namespace AgentForge.Verticals.Travel.Services;

public class PromotionService
{
    private readonly List<Promotion> _promotions;

    public PromotionService(IConfiguration? configuration = null)
    {
        var json = TravelDataFiles.ReadAllText("Promotions.json", configuration);
        _promotions = JsonSerializer.Deserialize<List<Promotion>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    public IReadOnlyList<Promotion> GetActivePromotions()
    {
        var today = DateTime.UtcNow.Date;
        return _promotions
            .Where(p => DateTime.Parse(p.ValidFrom).Date <= today && DateTime.Parse(p.ValidUntil).Date >= today)
            .ToList();
    }

    public IReadOnlyList<Promotion> GetAll() => _promotions;
}
