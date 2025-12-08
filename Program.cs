using CFACalculateWebAPI.Data;
using CFACalculateWebAPI.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ----------------------
// Add services
// ----------------------
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Allow CORS for testing
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Database context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Audit service
builder.Services.AddScoped<AuditDataService>();

// ----------------------
// Build app
// ----------------------
var app = builder.Build();

// ----------------------
// Middleware
// ----------------------
app.UseCors("AllowAll");
app.UseHttpsRedirection();
app.UseAuthorization();

// Serve static files if any
app.UseDefaultFiles();
app.UseStaticFiles();

// ----------------------
// Swagger (for virtual directory /DDCFACAL_API)
// ----------------------
app.UseSwagger(c =>
{
    // If hosting under virtual directory, set the route prefix
    c.RouteTemplate = "swagger/{documentName}/swagger.json";
});

app.UseSwaggerUI(c =>
{
    // The JSON endpoint is relative to the Swagger UI page
    c.SwaggerEndpoint("./v1/swagger.json", "CFA Calculate API v1");
    c.RoutePrefix = "swagger"; // Swagger UI will be at /DDCFACAL_API/swagger
});

// ----------------------
// Map controllers
// ----------------------
app.MapControllers();

// ----------------------
// Run app
// ----------------------
app.Run();
