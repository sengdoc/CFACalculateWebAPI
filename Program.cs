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

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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
app.UseRouting();
app.UseAuthorization();

// Serve static files
app.UseDefaultFiles();   // optional, for index.html at root
app.UseStaticFiles();

// Swagger - only in development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "CFA Calculate API v1");
        c.RoutePrefix = "."; // Open Swagger at root
    });
}

// Map controllers
app.MapControllers();

// Run the app
app.Run();
