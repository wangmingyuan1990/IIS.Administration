// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.


namespace Microsoft.IIS.Administration.Tests
{
    using WebServer;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text;
    using Xunit;
    using Xunit.Abstractions;
    using Core.Utils;
    using System.Net;

    public class Sites
    {
        private const string TEST_SITE_NAME = "test_site";
        private const int TEST_PORT = 50306;
        private ITestOutputHelper _output;
        private static readonly object TEST_SITE = new {
            physical_path = TEST_SITE_PATH,
            name = TEST_SITE_NAME,
            bindings = new object[] {
                new {
                    ip_address = "*",
                    port = TEST_PORT,
                    protocol = "http"
                }
            }
        };

        public const string TEST_SITE_PATH = @"c:\sites\test_site";
        public static readonly string SITE_URL = $"{Configuration.TEST_SERVER_URL}/api/webserver/websites";
        public static readonly string CertificatesUrl = $"{Configuration.TEST_SERVER_URL}/api/certificates";

        public Sites(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CreateAndCleanup()
        {
            using (HttpClient client = ApiHttpClient.Create()) {

                _output.WriteLine($"Running tests with site: {TEST_SITE}");

                EnsureNoSite(client, TEST_SITE_NAME);

                JObject site;

                Assert.True(CreateSite(client, JsonConvert.SerializeObject(TEST_SITE), out site));
                _output.WriteLine("Create Site success.");

                string testSiteUri = $"{SITE_URL}/{site.Value<string>("id")}";

                Assert.True(SiteExists(client, testSiteUri));
                _output.WriteLine("Site Exists success.");

                Assert.True(DeleteSite(client, testSiteUri));
                _output.WriteLine("Delete Site success.");
            }
        }

        [Fact]
        public void ChangeAllProperties()
        {
            using (HttpClient client = ApiHttpClient.Create()) {

                EnsureNoSite(client, TEST_SITE_NAME);
                JObject site = CreateSite(client, TEST_SITE_NAME, TEST_PORT, @"c:\sites\test_site");
                JObject cachedSite = new JObject(site);

                WaitForStatus(client, site);

                Assert.True(site != null);

                site["server_auto_start"] = !site.Value<bool>("server_auto_start");
                site["physical_path"] = @"c:\sites";
                site["enabled_protocols"] = "test_protocol";

                // If site status is unknown then we don't know if it will be started or stopped when it becomes available
                // Utilizing the defaults we assume it will go from unkown to started
                site["status"] = Enum.GetName(typeof(Status),
                                                             DynamicHelper.To<Status>(site["status"]) ==
                                                             Status.Stopped ? Status.Started :
                                                             Status.Stopped);

                JObject limits = (JObject)site["limits"];
                limits["connection_timeout"] = limits.Value<long>("connection_timeout") - 1;
                limits["max_bandwidth"] = limits.Value<long>("max_bandwidth") - 1;
                limits["max_connections"] = limits.Value<long>("max_connections") - 1;
                limits["max_url_segments"] = limits.Value<long>("max_url_segments") - 1;
                
                JArray bindings = site.Value<JArray>("bindings");
                bindings.Clear();
                bindings.Add(JObject.FromObject(new {
                    port = 63014,
                    ip_address = "40.3.5.15",
                    hostname = "testhostname",
                    protocol = "http"
                }));
                bindings.Add(JObject.FromObject(new {
                    port = 63015,
                    ip_address = "*",
                    hostname = "",
                    protocol = "https",
                    certificate = GetCertificate(client)
                }));

                string result;
                string body = JsonConvert.SerializeObject(site);

                Assert.True(client.Patch(Utils.Self(site), body, out result));

                JObject newSite = JsonConvert.DeserializeObject<JObject>(result);

                Assert.True(Utils.JEquals<bool>(site, newSite, "server_auto_start"));
                Assert.True(Utils.JEquals<string>(site, newSite, "physical_path"));
                Assert.True(Utils.JEquals<string>(site, newSite, "enabled_protocols"));
                Assert.True(Utils.JEquals<string>(site, newSite, "status", StringComparison.OrdinalIgnoreCase));

                Assert.True(Utils.JEquals<long>(site, newSite, "limits.connection_timeout"));
                Assert.True(Utils.JEquals<long>(site, newSite, "limits.max_bandwidth"));
                Assert.True(Utils.JEquals<long>(site, newSite, "limits.max_connections"));
                Assert.True(Utils.JEquals<long>(site, newSite, "limits.max_url_segments"));

                for (var i = 0; i < bindings.Count; i++) {
                    var oldBinding = (JObject)bindings[i];
                    var newBinding = (JObject)bindings[i];

                    Assert.True(Utils.JEquals<string>(oldBinding, newBinding, "protocol"));
                    Assert.True(Utils.JEquals<string>(oldBinding, newBinding, "port"));
                    Assert.True(Utils.JEquals<string>(oldBinding, newBinding, "ip_address"));
                    Assert.True(Utils.JEquals<string>(oldBinding, newBinding, "hostname"));

                    if (newBinding.Value<string>("protocol").Equals("https")) {
                        Assert.True(JToken.DeepEquals(oldBinding["certificate"], newBinding["certificate"]));
                    }
                }

                    Assert.True(DeleteSite(client, Utils.Self(site)));
            }
        }
        [Fact]
        public void BindingConflict()
        {
            string[] httpProperties = new string[] { "ip_address", "port", "hostname", "protocol", "binding_information" };
            string[] httpsProperties = new string[] { "ip_address", "port", "hostname", "protocol", "binding_information", "certificate" };
            string[] othersProperties = new string[] { "protocol", "binding_information" };


            using (HttpClient client = ApiHttpClient.Create()) {
                EnsureNoSite(client, TEST_SITE_NAME);
                JObject site = CreateSite(client, TEST_SITE_NAME, TEST_PORT, @"c:\sites\test_site");

                var bindings = site.Value<JArray>("bindings");
                bindings.Clear();

                var conflictBindings = new object[] {
                    new {
                        port = 63015,
                        ip_address = "*",
                        hostname = "abc",
                        protocol = "http"
                    },
                    new {
                        port = 63015,
                        ip_address = "*",
                        hostname = "abc",
                        protocol = "http"
                    }
                };

                foreach (var b in conflictBindings) {
                    bindings.Add(JObject.FromObject(b));
                }

                var response = client.PatchRaw(Utils.Self(site), site);
                Assert.True(response.StatusCode == HttpStatusCode.Conflict);

                conflictBindings = new object[] {
                    new {
                        binding_information = "35808:*",
                        protocol = "net.tcp"
                    },
                    new {
                        binding_information = "35808:*",
                        protocol = "net.tcp"
                    }
                };

                bindings.Clear();
                foreach (var b in conflictBindings) {
                    bindings.Add(JObject.FromObject(b));
                }

                response = client.PatchRaw(Utils.Self(site), site);
                Assert.True(response.StatusCode == HttpStatusCode.Conflict);


                Assert.True(DeleteSite(client, Utils.Self(site)));
            }
        }


        [Fact]
        public void BindingTypes()
        {
            string[] httpProperties = new string[] { "ip_address", "port", "hostname", "protocol", "binding_information" };
            string[] httpsProperties = new string[] { "ip_address", "port", "hostname", "protocol", "binding_information", "certificate" };
            string[] othersProperties = new string[] { "protocol", "binding_information" };


            using (HttpClient client = ApiHttpClient.Create()) {

                EnsureNoSite(client, TEST_SITE_NAME);
                JObject site = CreateSite(client, TEST_SITE_NAME, TEST_PORT, @"c:\sites\test_site");

                var bindings = site.Value<JArray>("bindings");
                bindings.Clear();
                int p = 63013;

                var goodBindings = new object[] {
                    new {
                        port = p++,
                        ip_address = "*",
                        hostname = "abc",
                        protocol = "http"
                    },
                    new {
                        binding_information = "128.0.3.5:" + (p++) + ":def",
                        protocol = "http"
                    },
                    new {
                        port = p++,
                        ip_address = "*",
                        hostname = "",
                        protocol = "https",
                        certificate = GetCertificate(client)
                    },
                    new {
                        binding_information = "*:" + (p++) + ":def",
                        protocol = "http"
                    },
                    new {
                        binding_information = (p++) + ":*",
                        protocol = "net.tcp"
                    },
                    new {
                        binding_information = "*",
                        protocol = "net.pipe"
                    }
                };

                foreach (var b in goodBindings) {
                    bindings.Add(JObject.FromObject(b));
                }

                var res = client.Patch(Utils.Self(site), site);
                Assert.NotNull(res);

                JArray newBindings = res.Value<JArray>("bindings");
                Assert.True(bindings.Count == newBindings.Count);

                for (var i = 0; i < bindings.Count; i++) {
                    var binding = (JObject)bindings[i];
                    foreach (var prop in binding.Properties()) {
                        Assert.True(JToken.DeepEquals(binding[prop.Name], newBindings[i][prop.Name]));
                    }

                    string protocol = binding.Value<string>("protocol");

                    switch (protocol) {
                        case "http":
                            Assert.True(HasExactProperties((JObject)newBindings[i], httpProperties));
                            break;
                        case "https":
                            Assert.True(HasExactProperties((JObject)newBindings[i], httpsProperties));
                            break;
                        default:
                            Assert.True(HasExactProperties((JObject)newBindings[i], othersProperties));
                            break;
                    }
                }

                var badBindings = new object[] {
                    new {
                        port = p++,
                        ip_address = "*",
                        hostname = "abc"
                    },
                    new {
                        port = p++,
                        ip_address = "",
                        hostname = "abc",
                        protocol = "http"
                    },
                    new {
                        protocol = "http",
                        binding_information = $":{p++}:"
                    },
                    new {
                        protocol = "http",
                        binding_information = $"127.0.4.3::"
                    }
                };

                foreach (var badBinding in badBindings) {
                    newBindings.Clear();
                    newBindings.Add(JObject.FromObject(badBinding));
                    var response = client.PatchRaw(Utils.Self(res), res);
                    Assert.True(response.StatusCode == HttpStatusCode.BadRequest);
                }

                Assert.True(DeleteSite(client, Utils.Self(site)));
            }
        }

        private bool HasExactProperties(JObject obj, IEnumerable<string> names) {
            if (obj.Properties().Count() != names.Count()) {
                return false;
            }

            foreach (var property in obj.Properties()) {
                if (!names.Contains(property.Name)) {
                    return false;
                }
            }
            return true;
        }

        [Theory]
        [InlineData(10)]
        public void GetSites(int n)
        {
            using (HttpClient client = ApiHttpClient.Create()) {

                string result;
                for(int i = 0; i < n; i++) {
                    
                    Assert.True(client.Get(SITE_URL, out result));
                }
            }
        }

        public static bool CreateSite(HttpClient client, string testSite, out JObject site)
        {
            site = null;
            HttpContent content = new StringContent(testSite, Encoding.UTF8, "application/json");
            HttpResponseMessage response = client.PostAsync(SITE_URL, content).Result;

            if (!Globals.Success(response)) {
                return false;
            }

            site = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

            return true;
        }

        public static JObject CreateSite(HttpClient client, string name, int port, string physicalPath)
        {

            var site = new {
                name = name,
                physical_path = physicalPath,
                bindings = new object[] {
                    new {
                        ip_address = "*",
                        port = port.ToString(),
                        protocol = "http"
                    }
                }
            };

            string siteStr = JsonConvert.SerializeObject(site);

            JObject result;
            if(!CreateSite(client, siteStr, out result)) {
                throw new Exception();
            }
            return result;
        }

        public static bool DeleteSite(HttpClient client, string siteUri)
        {
            if (!SiteExists(client, siteUri)) { throw new Exception("Can't delete test site because it doesn't exist."); }
            HttpResponseMessage response = client.DeleteAsync(siteUri).Result;
            return Globals.Success(response);
        }

        public static bool GetSites(HttpClient client, out List<JObject> sites)
        {
            string response = null;
            sites = null;

            if(!client.Get(SITE_URL, out response)) {
                return false;
            }

            JObject jObj = JsonConvert.DeserializeObject<JObject>(response);

            JArray sArr = jObj["websites"] as JArray;
            sites = new List<JObject>();

            foreach(JObject site in sArr) {
                sites.Add(site);
            }

            return true;
        }

        public static JObject GetSite(HttpClient client, string siteName)
        {
            List<JObject> sites;

            if (!(GetSites(client, out sites))) {
                return null;
            }

            JObject siteRef =  sites.FirstOrDefault(s => {
                string name = DynamicHelper.Value(s["name"]);

                return name == null ? false : name.Equals(siteName, StringComparison.OrdinalIgnoreCase);
            });

            if(siteRef == null) {
                return null;
            }

            string siteContent;
            if(client.Get($"{Configuration.TEST_SERVER_URL}{ siteRef["_links"]["self"].Value<string>("href") }", out siteContent)) {

                return JsonConvert.DeserializeObject<JObject>(siteContent);
            }

            return null;
        }

        public static void EnsureNoSite(HttpClient client, string siteName)
        {
            JObject site = GetSite(client, siteName);

            if (site == null) {
                return;
            }

            if(!DeleteSite(client, Utils.Self(site))) {
                throw new Exception();
            }
        }

        private static bool SiteExists(HttpClient client, string siteUri)
        {
            HttpResponseMessage responseMessage = client.GetAsync(siteUri).Result;
            return Globals.Success(responseMessage);
        }

        private void WaitForStatus(HttpClient client, JObject site)
        {
            string res;
            int refreshCount = 0;
            while (site.Value<string>("status") == "unknown") {
                refreshCount++;
                if (refreshCount > 100) {
                    throw new Exception();
                }

                client.Get(Utils.Self(site), out res);
                site = JsonConvert.DeserializeObject<JObject>(res);
            }
        }

        private static JObject GetCertificate(HttpClient client)
        {
            string result;
            if (!client.Get(CertificatesUrl, out result)) {
                return null;
            }

            var certsObj = JObject.Parse(result);
            var cert = certsObj.Value<JArray>("certificates").FirstOrDefault();

            return cert != null ? cert.ToObject<JObject>() : null;
        }
    }
}
