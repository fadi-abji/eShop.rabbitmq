using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var messaging = builder.AddRabbitMQ("messaging");

// Databases

var postgres = builder.AddPostgres("postgres").WithPgAdmin();
var catalogDb = postgres.AddDatabase("CatalogDB");


// Cache
var redis = builder.AddRedis("cache");

// DB Manager Apps

builder.AddProject<Catalog_Data_Manager>("catalog-db-mgr")
    .WithReference(catalogDb);


// API Apps

var catalogApi = builder.AddProject<Catalog_API>("catalog-api")
    .WithReference(catalogDb)
    .WithReference(messaging);

// Apps

builder.AddProject<WebApp>("webapp")
    .WithReference(catalogApi);
//add the RabbitConsumer project to .NET Aspire orchestration, add the following code:
builder.AddProject<RabbitConsumer>("consumers")
    .WithReference(messaging);

// Inject assigned URLs for Catalog API
catalogApi.WithEnvironment("CatalogOptions__PicBaseAddress", () => catalogApi.GetEndpoint("http").UriString);

builder.Build().Run();
