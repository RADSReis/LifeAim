using LifeAim.Modules.Agents;
using LifeAim.Modules.Communication;
using LifeAim.Modules.Goals;
using LifeAim.Modules.Planning;
using LifeAim.Modules.Progress;
using LifeAim.Modules.Reporting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services
    .AddGoalsModule()
    .AddPlanningModule()
    .AddProgressModule()
    .AddAgentsModule()
    .AddCommunicationModule()
    .AddReportingModule();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
