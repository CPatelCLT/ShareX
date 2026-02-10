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
using System.Windows.Forms;

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
                DirectURL = config.ImmichDirectURL,
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
        public bool DirectURL { get; set; } = true;
        public bool UseAlbum { get; set; }
        public string Album { get; set; }

        // Server config with external domain for share links
        private ImmichServerConfig _serverConfig;
        private string _cachedExternalDomain;

        /// <summary>
        /// Initializes a new instance of the ImmichUploader class.
        /// </summary>
        /// <param name="baseUrl">The base URL of the Immich server.</param>
        /// <param name="apiKey">The API key for authentication.</param>
        public ImmichUploader(string baseUrl, string apiKey)
        {
            // Trim trailing slash to ensure consistent URL construction
            BaseUrl = baseUrl?.TrimEnd('/');
            ApiKey = apiKey;
        }

        /// <summary>
        /// Gets the server configuration including external domain for share links.
        /// </summary>
        /// <returns>The server configuration, or null if the request fails.</returns>
        private ImmichServerConfig GetServerConfig()
        {
            try
            {
                string url = URLHelpers.CombineURL(BaseUrl, "api/server/config");
                NameValueCollection headers = GetAuthHeaders();

                string response = SendRequest(HttpMethod.GET, url, headers: headers);

                if (!string.IsNullOrEmpty(response))
                {
                    return JsonConvert.DeserializeObject<ImmichServerConfig>(response);
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Error getting Immich server config");
            }

            return null;
        }

        /// <summary>
        /// Gets the external domain for share links.
        /// Returns null if not configured or if server config cannot be retrieved.
        /// </summary>
        /// <returns>The external domain URL, or null if not configured.</returns>
        private string GetExternalDomain()
        {
            // Return cached value if already fetched
            if (_cachedExternalDomain is not null)
            {
                return _cachedExternalDomain;
            }

            if (_serverConfig is null)
            {
                _serverConfig = GetServerConfig();
            }

            string domain = _serverConfig?.externalDomain?.TrimEnd('/');
            
            // Validate external domain format
            if (!string.IsNullOrEmpty(domain) && !domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                domain = $"https://{domain}";
            }

            // Cache the result
            _cachedExternalDomain = domain;
            return domain;
        }

        /// <summary>
        /// Checks if the API key is valid and has upload permissions by calling the /api/users/me endpoint
        /// </summary>
        /// <returns>True if the API key is valid, otherwise false.</returns>
        public bool CheckApiPermissions()
        {
            if (string.IsNullOrEmpty(BaseUrl) || string.IsNullOrEmpty(ApiKey))
            {
                Errors.Add("Immich base URL and API key are required.");
                return false;
            }

            try
            {
                string url = URLHelpers.CombineURL(BaseUrl, "api/users/me");
                NameValueCollection headers = GetAuthHeaders();

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
            catch (Exception ex)
            {
                Errors.Add($"Error checking Immich API permissions: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Uploads an image stream to Immich.
        /// </summary>
        /// <param name="stream">The image stream to upload.</param>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>The upload result containing the response and URL.</returns>
        public override UploadResult Upload(Stream stream, string fileName)
        {
            if (!CheckApiPermissions())
            {
                return null;
            }

            UploadResult result = UploadImageInternal(stream, fileName);

            if (result is not null && result.IsSuccess && !string.IsNullOrEmpty(result.Response))
            {
                try
                {
                    ImmichUploadResponse uploadResponse = JsonConvert.DeserializeObject<ImmichUploadResponse>(result.Response);
                    if (uploadResponse is not null && !string.IsNullOrEmpty(uploadResponse.id))
                    {
                        // Add to album if specified
                        if (UseAlbum && !string.IsNullOrEmpty(Album))
                        {
                            AddAssetToAlbum(uploadResponse.id, Album);
                        }

                        // Set the URL based on DirectURL setting
                        if (DirectURL)
                        {
                            string directUrl = CreateDirectLinkForAsset(uploadResponse.id);
                            if (!string.IsNullOrEmpty(directUrl))
                            {
                                result.URL = directUrl;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugHelper.WriteException(ex, "Error processing Immich upload response");
                }
            }

            return result;
        }

        /// <summary>
        /// Uploads an image to the Immich assets endpoint.
        /// </summary>
        /// <param name="stream">The image stream to upload.</param>
        /// <param name="fileName">The name of the file.</param>
        /// <returns>The upload result from the API.</returns>
        private UploadResult UploadImageInternal(Stream stream, string fileName)
        {
            string url = URLHelpers.CombineURL(BaseUrl, "api/assets");
            NameValueCollection headers = GetAuthHeaders();

            // Generate unique device identifiers
            string deviceId = Environment.MachineName;
            string deviceAssetId = Guid.NewGuid().ToString();
            DateTime now = DateTime.UtcNow;

            Dictionary<string, string> args = new Dictionary<string, string>
            {
                { "deviceAssetId", deviceAssetId },
                { "deviceId", deviceId },
                { "fileCreatedAt", now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "fileModifiedAt", now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") }
            };

            UploadResult result = SendRequestFile(url, stream, fileName, "assetData", args, headers);

            return result;
        }

        /// <summary>
        /// Creates a shared link for the asset and returns the share URL.
        /// </summary>
        /// <param name="assetId">The ID of the asset to share.</param>
        /// <returns>A shared link URL to view the asset, or null if the operation fails.</returns>
        /// <exception cref="ArgumentException">Thrown when assetId is null or empty.</exception>
        private string CreateDirectLinkForAsset(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                throw new ArgumentException("Asset ID cannot be null or empty.", nameof(assetId));
            }

            try
            {
                string url = URLHelpers.CombineURL(BaseUrl, "api/shared-links");
                NameValueCollection headers = GetAuthHeaders();
                headers.Add("Content-Type", "application/json");

                ImmichSharedLinkRequest request = new ImmichSharedLinkRequest
                {
                    type = "INDIVIDUAL",
                    assetIds = new List<string> { assetId },
                    allowDownload = false,
                    showMetadata = false
                };

                string json = JsonConvert.SerializeObject(request);
                string response = SendRequest(HttpMethod.POST, url, json, "application/json", headers: headers);

                if (!string.IsNullOrEmpty(response))
                {
                    ImmichSharedLinkResponse linkResponse = JsonConvert.DeserializeObject<ImmichSharedLinkResponse>(response);
                    if (linkResponse is not null && !string.IsNullOrEmpty(linkResponse.key))
                    {
                        // Use external domain if configured, otherwise fall back to base URL
                        string domain = GetExternalDomain() ?? BaseUrl;
                        // Return the shared link URL in the format /share/{key}
                        return URLHelpers.CombineURL(domain, $"share/{linkResponse.key}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Error creating Immich shared link");
            }

            return null;
        }

        /// <summary>
        /// Gets the authentication headers for API requests.
        /// </summary>
        /// <returns>A collection containing the x-api-key header.</returns>
        private NameValueCollection GetAuthHeaders()
        {
            NameValueCollection headers = new NameValueCollection();
            headers.Add("x-api-key", ApiKey);
            return headers;
        }

        /// <summary> 
        /// Gets a list of albums from Immich.
        /// </summary>
        /// <returns>A list of albums, or an empty list if the request fails.</returns>
        public List<ImmichAlbum> GetAlbums()
        {
            List<ImmichAlbum> albums = new List<ImmichAlbum>();

            try
            {
                string url = URLHelpers.CombineURL(BaseUrl, "api/albums");
                NameValueCollection headers = GetAuthHeaders();

                string response = SendRequest(HttpMethod.GET, url, headers: headers);

                if (!string.IsNullOrEmpty(response))
                {
                    List<ImmichAlbumResponse> albumResponses = JsonConvert.DeserializeObject<List<ImmichAlbumResponse>>(response);
                    if (albumResponses is not null)
                    {
                        foreach (var album in albumResponses)
                        {
                            if (!string.IsNullOrEmpty(album.id) && !string.IsNullOrEmpty(album.albumName))
                            {
                                albums.Add(new ImmichAlbum { Id = album.id, Name = album.albumName });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Error getting Immich albums");
            }

            return albums;
        }

        /// <summary>
        /// Tests the API connection to Immich.
        /// </summary>
        /// <param name="baseUrl">The base URL of the Immich server.</param>
        /// <param name="apiKey">The API key for authentication.</param>
        /// <returns>A tuple containing success status and message.</returns>
        public static (bool success, string message) TestApiConnection(string baseUrl, string apiKey)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            {
                return (false, "Please enter URL and API key");
            }

            try
            {
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
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
                return (false, "API connection failed");
            }
        }

        /// <summary>
        /// Loads albums from Immich.
        /// </summary>
        /// <param name="baseUrl">The base URL of the Immich server.</param>
        /// <param name="apiKey">The API key for authentication.</param>
        /// <returns>A tuple containing the list of albums and a status message.</returns>
        public static (List<ImmichAlbum> albums, string message) LoadAlbums(string baseUrl, string apiKey)
        {
            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey))
            {
                return (null, "Please enter URL and API key");
            }

            try
            {
                ImmichUploader uploader = new ImmichUploader(baseUrl, apiKey);
                List<ImmichAlbum> albums = uploader.GetAlbums();

                if (albums != null && albums.Count > 0)
                {
                    return (albums, $"Loaded {albums.Count} albums");
                }
                else
                {
                    return (albums, "No albums found");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex);
                return (null, "Failed to load albums");
            }
        }

        /// <summary>
        /// Adds an asset to a specific album.
        /// </summary>
        /// <param name="assetId">The ID of the asset to add.</param>
        /// <param name="albumId">The ID of the album to add the asset to.</param>
        /// <returns>True if successful, otherwise false.</returns>
        /// <exception cref="ArgumentException">Thrown when assetId or albumId is null or empty.</exception>
        public bool AddAssetToAlbum(string assetId, string albumId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                throw new ArgumentException("Asset ID cannot be null or empty.", nameof(assetId));
            }

            if (string.IsNullOrWhiteSpace(albumId))
            {
                throw new ArgumentException("Album ID cannot be null or empty.", nameof(albumId));
            }

            try
            {
                string url = URLHelpers.CombineURL(BaseUrl, $"api/albums/{albumId}/assets");
                NameValueCollection headers = GetAuthHeaders();
                headers.Add("Content-Type", "application/json");

                var requestBody = new
                {
                    ids = new List<string> { assetId }
                };

                string json = JsonConvert.SerializeObject(requestBody);
                string response = SendRequest(HttpMethod.PUT, url, json, "application/json", headers: headers);

                return !string.IsNullOrEmpty(response);
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Error adding asset to Immich album");
                return false;
            }
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
        public List<string> assetIds { get; set; }
        public bool allowDownload { get; set; }
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
