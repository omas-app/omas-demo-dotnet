using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Omas.DemoVendor.ApiClient
{
    public partial class OmasApiClient
    {
        partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
        {
            //hack for AIP-122 resource name "resource/{slug}" in path
            var queryIndex = urlBuilder.Length;
            for (int e = queryIndex - 1; e >= 0 ; e--) {
                if(urlBuilder[e] == '?')
                {
                    queryIndex = e;
                    break;
                }
            }

            urlBuilder.Replace("%2F", "/", 0, queryIndex);
        }
    }
}
