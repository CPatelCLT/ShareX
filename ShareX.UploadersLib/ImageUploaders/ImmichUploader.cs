#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Newtonsoft.Json;
using ShareX.HelpersLib;
using ShareX.UploadersLib.Properties;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static Org.BouncyCastle.Math.EC.ECCurve;

namespace ShareX.UploadersLib.ImageUploaders
{
    public class ImmichImageUploaderService : ImageUploaderService
    {
        public override ImageDestination EnumValue { get; } = ImageDestination.Immich;

        public override Icon ServiceIcon => Resources.Immich;

        public override bool CheckConfig(UploadersConfig config)
        {
            return !string.IsNullOrEmpty(config.ImmichBaseUrl) && !string.IsNullOrEmpty(config.ImmichApiKey);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new ImmichUploader(config.ImmichBaseUrl, config.ImmichApiKey)
            {
                UseSlugs = config.ImmichUseSlugs,
                UseAlbum = config.ImmichUseAlbum,
                Album = config.ImmichAlbum
            };
        }

        public override TabPage GetUploadersConfigTabPage(UploadersConfigForm form) => form.tpImmich;
    }

    public sealed class ImmichUploader : ImageUploader
    {
        public string BaseUrl { get; set; }
        public string ApiKey { get; set; }
        public bool UseSlugs { get; set; }
        public bool UseAlbum { get; set; }
        public string Album { get; set; }

        private string _cachedExternalDomain;

        NameValueCollection headers = new NameValueCollection();

        public ImmichUploader(string baseUrl, string apiKey)
        {
            BaseUrl = baseUrl?.TrimEnd('/');
            ApiKey = apiKey;
            headers.Add("x-api-key", ApiKey);
        }

        // https://api.immich.app/endpoints/server/getServerConfig
        private string GetExternalDomain()
        {
            // Return cached value if already fetched
            if (_cachedExternalDomain is not null)
            {
                return _cachedExternalDomain;
            }

            string url = URLHelpers.CombineURL(BaseUrl, "api/server/config");
            string result = SendRequest(HttpMethod.GET, url, headers: headers);

            if (!string.IsNullOrEmpty(result))
            {
                ImmichServerConfig response = JsonConvert.DeserializeObject<ImmichServerConfig>(result);
                _cachedExternalDomain = response.externalDomain;
                return _cachedExternalDomain;
            }

            return null;
        }

        private bool CheckApiPermissions()
        {
            if (string.IsNullOrEmpty(BaseUrl) || string.IsNullOrEmpty(ApiKey))
            {
                Errors.Add("Immich base URL and API key are required.");
                return false;
            }

            string url = URLHelpers.CombineURL(BaseUrl, "api/users/me");
            string response = SendRequest(HttpMethod.GET, url, headers: headers);

            if (!string.IsNullOrEmpty(response))
            {
                ImmichUserResponse userResponse = JsonConvert.DeserializeObject<ImmichUserResponse>(response);
                if (userResponse is not null && !string.IsNullOrEmpty(userResponse.id))
                {
                    return true;
                }
            }

            Errors.Add("Failed to validate Immich API key. Please check your base URL and API key.");
            return false;
        }

        public override UploadResult Upload(Stream stream, string fileName)
        {
            if (!CheckApiPermissions())
            {
                return null;
            }

            // https://api.immich.app/endpoints/assets/uploadAsset
            string url = URLHelpers.CombineURL(BaseUrl, "api/assets");
            string deviceId = ShareXResources.UserAgent;
            string deviceAssetId = Guid.NewGuid().ToString();
            string now = DateTime.Now.ToString("O"); ;

            Dictionary<string, string> args = new Dictionary<string, string>
            {
                { "deviceAssetId", deviceAssetId },
                { "deviceId", deviceId },
                { "fileCreatedAt", now },
                { "fileModifiedAt", now }
            };

            ImmichUploadResponse uploadResponse = new ImmichUploadResponse();
            UploadResult result = SendRequestFile(url, stream, fileName, "assetData", args, headers);

            if (result?.Response != null)
            {
                uploadResponse = JsonConvert.DeserializeObject<ImmichUploadResponse>(result.Response);
            }

            
            if (UseAlbum && !string.IsNullOrEmpty(Album))
            {
                AddAssetToAlbum(uploadResponse.id, Album);
            }
            

            result.URL = CreateLink(uploadResponse.id, fileName);

            return result;
        }

        private string CreateLink(string assetId, string fileName)
        {
            string url = URLHelpers.CombineURL(BaseUrl, "api/shared-links");
            string domain = GetExternalDomain() ?? BaseUrl;
            headers.Add("Content-Type", "application/json");
            string slug = null;

            if (UseSlugs)
            {
                slug = fileName.Substring(0, (fileName.Length - 4));
            }

            ImmichSharedLinkRequest request = new ImmichSharedLinkRequest
            {
                type = "INDIVIDUAL",
                slug = slug,
                assetIds = new List<string> { assetId },
                allowDownload = true,
                allowUpload = false,
                showMetadata = true
            };
            ImmichSharedLinkResponse response = new ImmichSharedLinkResponse();

            string json = JsonConvert.SerializeObject(request);
            string result = SendRequest(HttpMethod.POST, url, json, "application/json", headers: headers);
            headers.Remove("Content-Type");

            if (!string.IsNullOrEmpty(result))
            {
                response = JsonConvert.DeserializeObject<ImmichSharedLinkResponse>(result);
                bool check = string.IsNullOrEmpty(response?.key);

                if (!check && UseSlugs)
                {
                    return domain + "/s/" + slug;
                }
                if (!check && !UseSlugs)
                {
                    return domain + "/share/photo/" + response.key + "/" + assetId + "/original";
                }

            }
            return null;
        }

        // https://api.immich.app/endpoints/albums/getAllAlbums
        // replace this for linq later...
        public List<ImmichAlbum> GetAlbums()
        {
            List<ImmichAlbum> albums = new List<ImmichAlbum>();
            List<ImmichAlbumResponse> albumResponses = new List<ImmichAlbumResponse>();

            string url = URLHelpers.CombineURL(BaseUrl, "api/albums");
            string response = SendRequest(HttpMethod.GET, url, headers: headers);

            if (!string.IsNullOrEmpty(response))
            {
                albumResponses = JsonConvert.DeserializeObject<List<ImmichAlbumResponse>>(response);
            }

            if (albumResponses != null)
            {
                foreach (var album in albumResponses)
                {
                    if (!string.IsNullOrEmpty(album?.id) && !string.IsNullOrEmpty(album?.albumName))
                    {
                        albums.Add(new ImmichAlbum { Id = album.id, Name = album.albumName });
                    }
                } 
            }

            return albums;
        }

        public static (bool success, string message) TestApiConnection(string baseUrl, string apiKey)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            {
                return (false, "Please enter URL and API key");
            }

            ImmichUploader uploader = new ImmichUploader(baseUrl, apiKey);
            bool isValid = uploader.CheckApiPermissions();

            if (isValid)
            {
                return (true, "API connection successful");
            }
            else
            {
                return (false, "API connection failed: " + string.Join(", ", uploader.Errors));
            }
        }

        public static (List<ImmichAlbum> albums, string message) LoadAlbums(string baseUrl, string apiKey)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            {
                return (null, "Please enter URL and API key");
            }

            ImmichUploader uploader = new ImmichUploader(baseUrl, apiKey);
            List<ImmichAlbum> albums = uploader.GetAlbums();

            if (albums != null && albums.Count > 0)
            {
                return (albums, $"Loaded {albums.Count} albums");
            }
            else
            {
                return (albums, "Albums not found or failed to load");
            }
        }

        public bool AddAssetToAlbum(string assetId, string albumId)
        {
            if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(albumId))
            {
                Errors.Add("Asset or Album ID cannot be null or empty.");
                return false;
            }

            string url = URLHelpers.CombineURL(BaseUrl, $"api/albums/{albumId}/assets");
            headers.Add("Content-Type", "application/json");

            var requestBody = new
            {
                ids = new List<string> { assetId }
            };

            string json = JsonConvert.SerializeObject(requestBody);
            string response = SendRequest(HttpMethod.PUT, url, json, "application/json", headers: headers);
            headers.Remove("Content-Type");

            return !string.IsNullOrEmpty(response);

        }
    }

    internal class ImmichUserResponse
    {
        public string id { get; set; }
    }

    internal class ImmichUploadResponse
    {
        public string id { get; set; }
    }

    internal class ImmichSharedLinkRequest
    {
        public string type { get; set; }
        public string slug { get; set; }
        public List<string> assetIds { get; set; }
        public bool allowDownload { get; set; }
        public bool allowUpload { get; set; }
        public bool showMetadata { get; set; }
    }

    internal class ImmichSharedLinkResponse
    {
        public string key { get; set; }
    }

    internal class ImmichAlbumResponse
    {
        public string id { get; set; }
        public string albumName { get; set; }
    }

    public class ImmichAlbum
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    internal class ImmichServerConfig
    {
        public string externalDomain { get; set; }
    }
}
