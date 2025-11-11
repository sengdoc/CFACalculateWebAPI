using CFACalculateWebAPI.Data;      // Your DbContext namespace
using CFACalculateWebAPI.Services;  // Your service namespace
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ----------------------
// Add services
// ----------------------

// Add controllers
builder.Services.AddControllers();

// Swagger / OpenAPI
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS - allow everything for testing
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Add DbContext (replace with your real connection string)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add your service
builder.Services.AddScoped<AuditDataService>();

// ----------------------
// Build app
// ----------------------
var app = builder.Build();

// ----------------------
// Middleware
// ----------------------
app.UseCors("AllowAll");           // Must come before routing
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

// Swagger - only in development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CFA Calculate API v1");
        c.RoutePrefix = ""; // Open Swagger at root: https://localhost:5001/
    });
}

// Map controllers
app.MapControllers();

// Run the app
app.Run();
