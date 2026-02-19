using Broadcast.Services;

var builder = WebApplication.CreateBuilder(args);

// BroadcastHub must be singleton: one shared instance holds the init segment
// and the per-viewer channel dictionary used by all controller requests.
builder.Services.AddSingleton<BroadcastHub>();
builder.Services.AddControllers();

var app = builder.Build();

// Serve wwwroot/index.html for GET /
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();
