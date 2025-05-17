
// Omas Demo Vendor over Restful JSON API 

using Duende.AccessTokenManagement;
using Duende.IdentityModel.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Omas.DemoVendor.ApiClient;
using System.Runtime.CompilerServices;

string vendorId = "demo-dendor";

string authTokenUrl = "https://auth.omas.app/realms/omas/protocol/openid-connect/token";
string authDeviceUrl = "https://auth.omas.app/realms/omas/protocol/openid-connect/auth/device";
string authClientId = "demo-client";
string authScope = "openid omas offline_access";

string apiServerUrl = "https://api.omas.app";


var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, sc) =>
    {
        sc.AddClientCredentialsTokenManagement()
            .AddClient("omas-api", o =>
            {
                o.TokenEndpoint = authTokenUrl;
                o.ClientId = authClientId;
                o.Scope = authScope;
            });

        sc.AddHttpClient<OmasApiClient>("omas-api", client =>
        {
            client.BaseAddress = new Uri(apiServerUrl);
        })
        .AddClientCredentialsTokenHandler("omas-api");

        sc.AddDistributedMemoryCache();
    })
    .Build();


await host.StartAsync();

var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

var cancel = lifetime.ApplicationStopping;

var logger = host.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("Omas.DemoVendor");

//we try to load the offline token
await LoadOrRegisterToken(cancel);


var client = host.Services.GetRequiredService<OmasApiClient>();

//query info endpoint
{
    var info = await client.InfoAsync(cancel);

    var authStatus = info?.User.Authenticated ?? false ? "authenticated" : "invalid";

    logger.LogInformation("auth: {authStatus}, motd: {message}", authStatus, info.Motd);
}


//monitor modified orders
{
    //nswag does not support streaming responses
    //see: https://github.com/RicoSuter/NSwag/issues/5168
    //client.MonitorOrdersAsync requires a custom implementation
}

//poll modified orders
await foreach(var fulfillment in PollOrders(cancel))
{
    logger.LogInformation("{order} received.", fulfillment.Name);

    switch(fulfillment.State)
    {
        case FulfillmentState.PENDING:
            //we need to acknowledge the order
            _ = await client.ConfirmOrderV1Async(fulfillment.Name, new ConfirmOrderRequest { Ack = new Empty() }, cancel);
            //we wait for updated order or we could aslo process the result
            break;
        case FulfillmentState.RECEIVED:
            {
                Console.WriteLine($"Do you want to accept the order {fulfillment.Name}?");
                Console.WriteLine("A: accept or D: decline");
                Console.Write("Enter your choice [D]:");

                var choice = Console.ReadKey();

                var accept = choice.KeyChar switch
                {
                    'A' or 'a' => true,
                    _ => false
                };

                if(accept)
                {
                    var now = DateTimeOffset.Now;

                    var body = new ConfirmOrderRequest
                    {
                        Accept = new ConfirmOrderRequestAccept
                        {
                            PackagingTime = now.AddSeconds(300), //some estimate
                            DeliveryTime = now.AddSeconds(3600) //some estimate
                        }
                    };

                    _ = await client.ConfirmOrderV1Async(fulfillment.Name, body, cancel);

                    logger.LogInformation("order accepted");

                    //start order fulfillment as Task
                    _ = FulfillOrder(fulfillment, cancel);
                } 
                else
                {
                    var body = new ConfirmOrderRequest
                    {
                        Decline = "vendor closed"
                    };

                    _ = await client.ConfirmOrderV1Async(fulfillment.Name, body, cancel);
                }
            }
            break;
        default:
            //ignore or reentry
            break;
    }
}

await host.StopAsync();

#region auth functions

async Task LoadOrRegisterToken(CancellationToken cancellationToken)
{
    var tokenFile = $"{authClientId}-token.jwt";
    string offlineToken;

    if (File.Exists(tokenFile))
    {
        offlineToken = await File.ReadAllTextAsync(tokenFile, cancel);

        offlineToken = await RefreshToken(offlineToken, cancel);
    }
    else
    {
        //we request a new device token
        offlineToken = await RegisterDevice(cancel);
    }

    await File.WriteAllTextAsync(tokenFile, offlineToken, cancel);
}


async Task<string> RegisterDevice(CancellationToken cancellationToken)
{
    // we need to register this device instance once to get an offline refresh_token

    logger.LogInformation("device register");

    var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient();

    var tokenCache = host.Services.GetRequiredService<IClientCredentialsTokenCache>();

    var deviceRequest = new DeviceAuthorizationRequest
    {
        Address = authDeviceUrl,
        Scope = authScope,
        ClientId = authClientId
    };

    var deviceRsp = await client.RequestDeviceAuthorizationAsync(deviceRequest, cancellationToken);

    if (deviceRsp.IsError)
        throw new Exception(deviceRsp.ErrorDescription);

    var deadline = deviceRsp.ExpiresIn.HasValue ? DateTime.UtcNow.AddSeconds(deviceRsp.ExpiresIn.Value) : DateTime.MaxValue;

    using var period = new PeriodicTimer(TimeSpan.FromSeconds(deviceRsp.Interval > 0 ? deviceRsp.Interval : 5) + TimeSpan.FromMicroseconds(500));

    logger.LogInformation("Go to: {url}\nEnter the code: {code}", deviceRsp.VerificationUri, deviceRsp.UserCode);

    var req = new DeviceTokenRequest
    {
        Address = authTokenUrl,
        ClientId = authClientId,
        DeviceCode = deviceRsp.DeviceCode,
    };

    TokenResponse rsp = null;

    while (DateTimeOffset.UtcNow < deadline)
    {
        await period.WaitForNextTickAsync(cancellationToken);

        rsp = await client.RequestDeviceTokenAsync(req, cancellationToken);

        if (!string.IsNullOrEmpty(rsp.AccessToken))
            break;        
        else if (rsp.IsError)
            logger.LogError(rsp.ErrorDescription);
    }

    if (rsp?.IsError ?? true)
        throw new Exception(rsp?.ErrorDescription ?? "token response required");

    await tokenCache.SetAsync("omas-api", new ClientCredentialsToken
    {
        AccessToken = rsp.AccessToken,
        AccessTokenType = rsp.TokenType,
        Expiration = DateTimeOffset.UtcNow.AddSeconds(rsp.ExpiresIn),
        Scope = rsp.Scope,
        //DPoPJsonWebKey
        Error = rsp.Error,

    }, new TokenRequestParameters { }, cancellationToken);

    return rsp.RefreshToken;
}

async Task<string> RefreshToken(string offlineToken, CancellationToken cancellationToken)
{
    logger.LogInformation("refresh token");

    var client = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient();

    var tokenCache = host.Services.GetRequiredService<IClientCredentialsTokenCache>();

    var req = new RefreshTokenRequest
    {
        Address = authTokenUrl,
        ClientId = authClientId,
        RefreshToken = offlineToken
    };

    var rsp = await client.RequestRefreshTokenAsync(req, cancel);

    if (rsp.IsError)
        throw new Exception(rsp.ErrorDescription);

    await tokenCache.SetAsync("omas-api", new ClientCredentialsToken
    {
        AccessToken = rsp.AccessToken,
        AccessTokenType = rsp.TokenType,
        Expiration = DateTimeOffset.UtcNow.AddSeconds(rsp.ExpiresIn),
        Scope = rsp.Scope,
        //DPoPJsonWebKey
        Error = rsp.Error,

    }, new TokenRequestParameters { }, cancellationToken);

    return rsp.RefreshToken;
}

#endregion


#region helper functions

async IAsyncEnumerable<Fulfillment> PollOrders([EnumeratorCancellation] CancellationToken cancellationToken)
{
    var parent = $"vendors/{vendorId}";

    var tokenFile = "poll-orders.token";
    var pageToken = string.Empty;

    //load page token
    if (File.Exists(tokenFile))
        pageToken = await File.ReadAllTextAsync(tokenFile, cancel);

    //min poll interval
    using var period = new PeriodicTimer(TimeSpan.FromSeconds(5)); 

    
    //the poll loop
    while (!cancellationToken.IsCancellationRequested)
    {
        PollOrdersResponse rsp;

        try
        {
            rsp = await client.PollOrdersV1Async(parent, pageToken: pageToken, cancellationToken: cancel);
        }
        catch (OperationCanceledException)
        {
            continue;
        }

        if (rsp.Fulfillments is not null)
        {
            foreach (var item in rsp.Fulfillments)
                yield return item;
        }

        if (pageToken != rsp.NextPageToken)
        {
            pageToken = rsp.NextPageToken;
            await File.WriteAllTextAsync(tokenFile, pageToken, cancel);
        }

        await period.WaitForNextTickAsync(cancellationToken);
    }
}

async Task FulfillOrder(Fulfillment fulfillment, CancellationToken cancellationToken)
{
    //helper function to simulate the order fulfillment process

    await ProcessOrder(fulfillment, cancellationToken);

    await DeliverOrder(fulfillment, cancellationToken);

    await CompleteOrder(fulfillment, cancellationToken);
}

async Task<Fulfillment> ProcessOrder(Fulfillment fulfillment, CancellationToken cancellationToken)
{
    logger.LogInformation("order idle simulation");

    //delay process
    await Task.Delay(Random.Shared.Next(5, 30) * 1000, cancellationToken);

    //start processing
    {
        var req = new ProcessOrderRequest
        {
            Completed = false
        };

        fulfillment = await client.ProcessOrderV1Async(fulfillment.Name, req, cancellationToken);
    }

    logger.LogInformation("order processing");

    //delay process
    await Task.Delay(Random.Shared.Next(5, 30) * 1000, cancellationToken);

    //stop processing
    {
        var req = new ProcessOrderRequest
        {
            Completed = true
        };

        fulfillment = await client.ProcessOrderV1Async(fulfillment.Name, req, cancellationToken);
    }

    logger.LogInformation("order processed");

    return fulfillment;
}

async Task<Fulfillment> DeliverOrder(Fulfillment fulfillment, CancellationToken cancellationToken)
{
    logger.LogInformation("order delay pickup simulation");

    //delay pickup
    await Task.Delay(Random.Shared.Next(5, 30) * 1000, cancellationToken);

    var now = DateTimeOffset.UtcNow;

    //start delivering (last mile delivery)
    {
        var req = new DeliverOrderRequest
        {
            Delivery = new Delivery
            {
                Time = now.AddSeconds(300) //new delivery estimate
            }
        };

        fulfillment = await client.DeliverOrderV1Async(fulfillment.Name, req, cancellationToken);
    }

    logger.LogInformation("order delivering");

    //delay pickup
    await Task.Delay(Random.Shared.Next(5, 30) * 1000, cancellationToken);

    //order is delivered
    {
        var req = new DeliverOrderRequest
        {
            Delivery = new Delivery
            {
                Time = DateTimeOffset.Now //the delivery time
            },
            Completed = true
        };

        fulfillment = await client.DeliverOrderV1Async(fulfillment.Name, req, cancellationToken);
    }

    logger.LogInformation("order delivered");

    return fulfillment;
}


async Task CompleteOrder(Fulfillment fulfillment, CancellationToken cancellationToken)
{
    //complete order
    {
        //we set the order to completing
        //we could still cancel or settle with different payment channel ourself

        var req = new CompleteOrderRequest
        {
            //Settlement = new Settlement
            //{
            //    Payment //we could change the payment method
            //}
        };

        
        fulfillment = await client.CompleteOrderV1Async(fulfillment.Name, req, cancellationToken);
    }

    logger.LogInformation("order completing");

    //the order will get finalized with the COMPLETED or SETTLED state
}

#endregion