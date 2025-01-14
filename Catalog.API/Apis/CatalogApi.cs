﻿using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using eShop.Catalog.API;
using eShop.Catalog.API.Model;
using eShop.Catalog.Data;
using Microsoft.Extensions.Caching.Distributed;
using System.Text;

namespace Microsoft.AspNetCore.Builder;

public static class CatalogApi
{
    private static readonly FileExtensionContentTypeProvider _fileContentTypeProvider = new();

    public static IEndpointRouteBuilder MapCatalogApi(this IEndpointRouteBuilder app)
    {
        // Routes for querying catalog items.
        app.MapGet("/items", GetAllItems);
        app.MapGet("/items/by", GetItemsByIds);
        app.MapGet("/items/{id:int}", GetItemById);
        app.MapGet("/items/by/{name:minlength(1)}", GetItemsByName);
        app.MapGet("/items/{catalogItemId:int}/pic", GetItemPictureById);

        // Routes for resolving catalog items by type and brand.
        app.MapGet("/items/type/{typeId}/brand/{brandId?}", GetItemsByBrandAndTypeId);
        app.MapGet("/items/type/all/brand/{brandId:int?}", GetItemsByBrandId);
        app.MapGet("/catalogtypes", async (CatalogDbContext context) => await context.CatalogTypes.OrderBy(x => x.Type).AsNoTracking().ToListAsync());
        app.MapGet("/catalogbrands", async (CatalogDbContext context) => await context.CatalogBrands.OrderBy(x => x.Brand).AsNoTracking().ToListAsync());

        return app;
    }

    //To use dependency injection to obtain the connection to RabbitMQ, modify the code to match the following lines:
    public static async Task<Results<Ok<PaginatedItems<CatalogItem>>, BadRequest<string>>> GetAllItems(
       [AsParameters] PaginationRequest paginationRequest,
       [AsParameters] CatalogServices services,
       RabbitMQ.Client.IConnection connection)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var totalItems = await services.DbContext.CatalogItems
            .LongCountAsync();

        //Immediately after that line, to create a RabbitMQ messaging channel, add this code:
        var channel = connection.CreateModel();


        //On the next line, to create a message queue, add this code:
        channel.QueueDeclare(queue: "catalogEvents",
                     durable: false,
                     exclusive: false,
                     autoDelete: false,
                     arguments: null);

        //On the next line, to send the message, add this code:
        var body = Encoding.UTF8.GetBytes("Getting all items in the catalog.");

        channel.BasicPublish(exchange: string.Empty,
                             routingKey: "catalogEvents",
                             mandatory: false,
                             basicProperties: null,
                             body: body);

        if(connection is null)
        {
            return TypedResults.BadRequest("Connection to RabbitMQ is not available.");
        }
        else
        {
            //Getting the data from Database
            var itemsOnPage = await services.DbContext.CatalogItems
                   .OrderBy(c => c.Name)
                   .Skip(pageSize * pageIndex)
                   .Take(pageSize)
                   .AsNoTracking()
                   .ToListAsync();

            ChangeUriPlaceholder(services.Options.Value, itemsOnPage);

            return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
        }
    }

    public static async Task<Ok<List<CatalogItem>>> GetItemsByIds(
        [AsParameters] CatalogServices services,
        int[] ids)
    {
        var items = await services.DbContext.CatalogItems
            .Where(item => ids.Contains(item.Id))
            .AsNoTracking()
            .ToListAsync();

        ChangeUriPlaceholder(services.Options.Value, items);

        return TypedResults.Ok(items);
    }

    public static async Task<Results<Ok<CatalogItem>, NotFound, BadRequest<string>>> GetItemById(
        [AsParameters] CatalogServices services,
        int id)
    {
        if (id <= 0)
        {
            return TypedResults.BadRequest("Id is not valid.");
        }

        var item = await services.DbContext.CatalogItems
            .Include(ci => ci.CatalogBrand)
            .AsNoTracking()
            .SingleOrDefaultAsync(ci => ci.Id == id);

        if (item == null)
        {
            return TypedResults.NotFound();
        }

        item.PictureUri = services.Options.Value.GetPictureUrl(item.Id);

        return TypedResults.Ok(item);
    }

    public static async Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByName(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        string name)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var totalItems = await services.DbContext.CatalogItems
            .Where(c => c.Name.StartsWith(name))
            .LongCountAsync();

        var itemsOnPage = await services.DbContext.CatalogItems
            .Where(c => c.Name.StartsWith(name))
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        ChangeUriPlaceholder(services.Options.Value, itemsOnPage);

        return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
    }

    public static async Task<Results<NotFound, PhysicalFileHttpResult>> GetItemPictureById(CatalogDbContext context, IWebHostEnvironment environment, int catalogItemId)
    {
        var item = await context.CatalogItems
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == catalogItemId);

        if (item is null)
        {
            return TypedResults.NotFound();
        }

        var path = GetFullPath(environment.ContentRootPath, item.PictureFileName);

        var imageFileExtension = Path.GetExtension(item.PictureFileName);
        _fileContentTypeProvider.TryGetContentType(imageFileExtension, out var contentType);
        var lastModified = File.GetLastWriteTimeUtc(path);

        return TypedResults.PhysicalFile(path, contentType, lastModified: lastModified);
    }

    public static async Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByBrandAndTypeId(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        int typeId,
        int? brandId)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var query = services.DbContext.CatalogItems.AsQueryable();
        query = query.Where(c => c.CatalogTypeId == typeId);

        if (brandId is not null)
        {
            query = query.Where(c => c.CatalogBrandId == brandId);
        }

        var totalItems = await query
            .LongCountAsync();

        var itemsOnPage = await query
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        ChangeUriPlaceholder(services.Options.Value, itemsOnPage);

        return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
    }

    public static async Task<Ok<PaginatedItems<CatalogItem>>> GetItemsByBrandId(
        [AsParameters] PaginationRequest paginationRequest,
        [AsParameters] CatalogServices services,
        int? brandId)
    {
        var pageSize = paginationRequest.PageSize;
        var pageIndex = paginationRequest.PageIndex;

        var query = (IQueryable<CatalogItem>)services.DbContext.CatalogItems;

        if (brandId is not null)
        {
            query = query.Where(ci => ci.CatalogBrandId == brandId);
        }

        var totalItems = await query
            .LongCountAsync();

        var itemsOnPage = await query
            .Skip(pageSize * pageIndex)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync();

        ChangeUriPlaceholder(services.Options.Value, itemsOnPage);

        return TypedResults.Ok(new PaginatedItems<CatalogItem>(pageIndex, pageSize, totalItems, itemsOnPage));
    }

    private static void ChangeUriPlaceholder(CatalogOptions options, List<CatalogItem> items)
    {
        foreach (var item in items)
        {
            item.PictureUri = options.GetPictureUrl(item.Id);
        }
    }

    public static string GetFullPath(string contentRootPath, string pictureFileName) =>
        Path.Combine(contentRootPath, "Pics", pictureFileName);
}
