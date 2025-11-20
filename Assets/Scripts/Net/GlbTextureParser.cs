using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ARMeshyDemo.Net
{
    /// <summary>
    /// Parses GLB files to extract texture URI references.
    /// Used when textures are external (not embedded in the binary).
    /// </summary>
    public static class GlbTextureParser
    {
        /// <summary>
        /// Extract texture URIs from GLB JSON chunk.
        /// Returns null if textures are embedded (not external).
        /// </summary>
        public static string[] ExtractTextureUris(byte[] glbBytes)
        {
            try
            {
                // GLB format: Header (12 bytes) + JSON Chunk + Binary Chunk
                // Header: magic (4) + version (4) + length (4)
                // Chunk: chunkLength (4) + chunkType (4) + chunkData

                if (glbBytes.Length < 20)
                {
                    Debug.LogWarning("[GlbTextureParser] File too small to be valid GLB");
                    return null;
                }

                // Read header
                uint magic = BitConverter.ToUInt32(glbBytes, 0);
                if (magic != 0x46546C67) // "glTF" in ASCII
                {
                    Debug.LogWarning("[GlbTextureParser] Invalid GLB magic number");
                    return null;
                }

                uint version = BitConverter.ToUInt32(glbBytes, 4);
                // uint fileLength = BitConverter.ToUInt32(glbBytes, 8);

                // Read first chunk (should be JSON)
                uint jsonLength = BitConverter.ToUInt32(glbBytes, 12);
                uint jsonType = BitConverter.ToUInt32(glbBytes, 16);

                if (jsonType != 0x4E4F534A) // "JSON" in ASCII
                {
                    Debug.LogWarning("[GlbTextureParser] First chunk is not JSON");
                    return null;
                }

                // Extract JSON string
                int jsonStart = 20;
                string json = Encoding.UTF8.GetString(glbBytes, jsonStart, (int)jsonLength);

                Debug.Log($"[GlbTextureParser] Extracted JSON (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}...");

                // Parse texture URIs from JSON
                return ParseTextureUrisFromJson(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GlbTextureParser] Error parsing GLB: {ex.Message}");
                return null;
            }
        }

        private static string[] ParseTextureUrisFromJson(string json)
        {
            var uris = new List<string>();

            try
            {
                // Find "images" array in JSON
                int imagesIndex = json.IndexOf("\"images\"");
                if (imagesIndex < 0)
                {
                    Debug.Log("[GlbTextureParser] No 'images' array found in JSON");
                    return null;
                }

                // Find the array start
                int arrayStart = json.IndexOf('[', imagesIndex);
                if (arrayStart < 0) 
                {
                    Debug.Log("[GlbTextureParser] No array start '[' found after 'images'");
                    return null;
                }

                // Find matching closing bracket
                int arrayEnd = FindMatchingBracket(json, arrayStart);
                if (arrayEnd < 0) 
                {
                    Debug.Log("[GlbTextureParser] Could not find matching ']' for images array");
                    return null;
                }

                string imagesJson = json.Substring(arrayStart, arrayEnd - arrayStart + 1);
                Debug.Log($"[GlbTextureParser] Images array content (first 500 chars): {imagesJson.Substring(0, Math.Min(500, imagesJson.Length))}");

                // Extract all "uri" values
                int searchFrom = 0;
                while (true)
                {
                    int uriIndex = imagesJson.IndexOf("\"uri\"", searchFrom);
                    if (uriIndex < 0) break;

                    // Find the value (skip colon and whitespace)
                    int valueStart = imagesJson.IndexOf('\"', uriIndex + 5);
                    if (valueStart < 0) break;
                    valueStart++; // Skip opening quote

                    int valueEnd = imagesJson.IndexOf('\"', valueStart);
                    if (valueEnd < 0) break;

                    string uri = imagesJson.Substring(valueStart, valueEnd - valueStart);
                    Debug.Log($"[GlbTextureParser] Found URI: {uri}");
                    
                    // Only add if it's an external URI (not a data URI or bufferView reference)
                    if (!string.IsNullOrEmpty(uri) && 
                        !uri.StartsWith("data:") && 
                        !uri.Contains("bufferView"))
                    {
                        uris.Add(uri);
                        Debug.Log($"[GlbTextureParser] Added external texture URI: {uri}");
                    }
                    else
                    {
                        Debug.Log($"[GlbTextureParser] Skipped URI (embedded or data URI): {uri.Substring(0, Math.Min(50, uri.Length))}...");
                    }

                    searchFrom = valueEnd + 1;
                }
                
                // ? NEW: Also check for bufferView references (textures embedded in binary)
                if (uris.Count == 0)
                {
                    Debug.LogWarning("[GlbTextureParser] No external URIs found. Textures may be embedded using bufferView.");
                    Debug.LogWarning("[GlbTextureParser] This means textures ARE in the GLB but glTFast failed to decode them.");
                }

                Debug.Log($"[GlbTextureParser] Total external texture URIs found: {uris.Count}");
                return uris.Count > 0 ? uris.ToArray() : null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[GlbTextureParser] Error parsing texture URIs: {ex.Message}");
                return null;
            }
        }

        private static int FindMatchingBracket(string text, int openBracketIndex)
        {
            int depth = 1;
            for (int i = openBracketIndex + 1; i < text.Length; i++)
            {
                if (text[i] == '[') depth++;
                else if (text[i] == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }
    }
}
