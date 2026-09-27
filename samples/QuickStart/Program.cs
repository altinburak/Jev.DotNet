// Triage a support ticket with one Jev request: set TYPESAFE_API_KEY, then `dotnet run`.
using System.ComponentModel;
using Jev.DotNet;

using var client = new JevClient();

var ticket = new
{
    subject = "Payouts failing",
    body = "Help! My payouts have been failing for 3 days and my rent is due tomorrow.",
    customer = new { plan = "Pro", tenure_months = 26 },
};

var result = await client.SystemOneAsync(
    state: new { ticket },
    questions: new Questions
    {
        ["team"] = Question.Choice<Team>("Which team should handle `ticket`?"),
        ["urgent"] = Question.Noul("Does `ticket.body` convey time-sensitive urgency?"),
        ["frustration"] = Question.Score(
            "How frustrated is the customer in `ticket.body`?",
            "Calm: neutral, no complaint",
            "Frustrated: clearly unhappy but polite",
            "Very angry: hostile or threatening to leave"),
    });

var team = result.Choice("team");
var urgent = result.Noul("urgent");
var frustration = result.Score("frustration");

Console.WriteLine($"Model:       {result.Model} ({result.Usage.InputTokens} input tokens)");
Console.WriteLine($"Team:        {team.As<Team>()} (confidence {team.Confidence:P0})");
Console.WriteLine($"Urgent:      {urgent.Noul:P0}");
Console.WriteLine($"Frustration: {frustration.Score:0.00} ≈ {frustration.Legend[frustration.MostLikelyLevel]}");

// Code owns the policy; Jev supplies the judgments. Tune thresholds on your own data.
var escalate = urgent.Noul > 0.8 && frustration.Score >= 1;
Console.WriteLine(escalate ? "→ Escalate to on-call" : "→ Normal queue");

enum Team
{
    [Description("Payments, payouts, invoicing, refunds")] Billing,
    [Description("Bugs, outages, integrations")] Technical,
    [Description("Pricing, upgrades, new accounts")] Sales,
}
