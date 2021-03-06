﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Forge;
using Autodesk.Forge.Model;
using Newtonsoft.Json.Linq;
using Server.Assembler.Domain.Entities;

namespace Server.Assembler.Api.Controllers
{
  public static class TreeNode
  {
    public static async Task<IList<JsTreeNode>> GetHubsAsync(Credentials credentials)
    {
      IList<JsTreeNode> nodes = new List<JsTreeNode>();

      // the API SDK
      var hubsApi = new HubsApi();
      hubsApi.Configuration.AccessToken = credentials.TokenInternal;

      var hubs = await hubsApi.GetHubsAsync();
      foreach (KeyValuePair<string, dynamic> hubInfo in new DynamicDictionaryItems(hubs.data))
      {
        // check the type of the hub to show an icon
        var nodeType = "hubs";
        switch ((string) hubInfo.Value.attributes.extension.type)
        {
          case "hubs:autodesk.core:Hub":
            nodeType = "hubs"; // if showing only BIM 360, mark this as 'unsupported'
            break;
          case "hubs:autodesk.a360:PersonalHub":
            nodeType = "personalHub"; // if showing only BIM 360, mark this as 'unsupported'
            break;
          case "hubs:autodesk.bim360:Account":
            nodeType = "bim360Hubs";
            break;
        }

        // create a treenode with the values
        var hubNode = new JsTreeNode(hubInfo.Value.links.self.href, hubInfo.Value.attributes.name, nodeType,
          !(nodeType == "unsupported"));
        nodes.Add(hubNode);
      }

      return nodes;
    }

    public static async Task<IList<JsTreeNode>> GetProjectsAsync(string href, Credentials credentials)
    {
      IList<JsTreeNode> nodes = new List<JsTreeNode>();

      // the API SDK
      var projectsApi = new ProjectsApi();
      projectsApi.Configuration.AccessToken = credentials.TokenInternal;

      // extract the hubId from the href
      var idParams = href.Split('/');
      var hubId = idParams[idParams.Length - 1];

      var projects = await projectsApi.GetHubProjectsAsync(hubId);
      foreach (KeyValuePair<string, dynamic> projectInfo in new DynamicDictionaryItems(projects.data))
      {
        // check the type of the project to show an icon
        var nodeType = "projects";
        switch ((string) projectInfo.Value.attributes.extension.type)
        {
          case "projects:autodesk.core:Project":
            nodeType = "a360projects";
            break;
          case "projects:autodesk.bim360:Project":
            nodeType = "bim360projects";
            break;
        }

        // create a treenode with the values
        var projectNode = new JsTreeNode(projectInfo.Value.links.self.href, projectInfo.Value.attributes.name, nodeType,
          true);
        nodes.Add(projectNode);
      }

      return nodes;
    }

    public static async Task<IList<JsTreeNode>> GetProjectContents(string href, Credentials credentials)
    {
      IList<JsTreeNode> nodes = new List<JsTreeNode>();

      // the API SDK
      var projectApi = new ProjectsApi();
      projectApi.Configuration.AccessToken = credentials.TokenInternal;

      // extract the hubId & projectId from the href
      var idParams = href.Split('/');
      var hubId = idParams[idParams.Length - 3];
      var projectId = idParams[idParams.Length - 1];

      var folders = await projectApi.GetProjectTopFoldersAsync(hubId, projectId);
      foreach (KeyValuePair<string, dynamic> folder in new DynamicDictionaryItems(folders.data))
        nodes.Add(new JsTreeNode(folder.Value.links.self.href, folder.Value.attributes.displayName, "folders", true));
      return nodes;
    }

    public static async Task<IList<JsTreeNode>> GetFolderContents(string href, Credentials credentials)
    {
      IList<JsTreeNode> nodes = new List<JsTreeNode>();

      // the API SDK
      var folderApi = new FoldersApi();
      folderApi.Configuration.AccessToken = credentials.TokenInternal;

      // extract the projectId & folderId from the href
      var idParams = href.Split('/');
      var folderId = idParams[idParams.Length - 1];
      var projectId = idParams[idParams.Length - 3];

      // check if folder specifies visible types
      JArray visibleTypes = null;
      var folder = (await folderApi.GetFolderAsync(projectId, folderId)).ToJson();
      if (folder.data.attributes != null && folder.data.attributes.extension != null &&
          folder.data.attributes.extension.data != null && !(folder.data.attributes.extension.data is JArray) &&
          folder.data.attributes.extension.data.visibleTypes != null)
      {
        visibleTypes = folder.data.attributes.extension.data.visibleTypes;
        visibleTypes.Add(
          "items:autodesk.bim360:C4RModel"); // C4R models are not returned on visibleTypes, therefore add them here
      }

      var folderContents = await folderApi.GetFolderContentsAsync(projectId, folderId);
      // the GET Folder Contents has 2 main properties: data & included (not always available)
      var folderData = new DynamicDictionaryItems(folderContents.data);
      var folderIncluded = folderContents.Dictionary.ContainsKey("included")
        ? new DynamicDictionaryItems(folderContents.included)
        : null;

      // let's start iterating the FOLDER DATA
      foreach (KeyValuePair<string, dynamic> folderContentItem in folderData)
      {
        // do we need to skip some items? based on the visibleTypes of this folder
        string extension = folderContentItem.Value.attributes.extension.type;
        if (extension.IndexOf("Folder") /*any folder*/ == -1 && visibleTypes != null &&
            !visibleTypes.ToString().Contains(extension)) continue;

        // if the type is items:autodesk.bim360:Document we need some manipulation...
        if (extension.Equals("items:autodesk.bim360:Document"))
        {
          // as this is a DOCUMENT, lets interate the FOLDER INCLUDED to get the name (known issue)
          foreach (KeyValuePair<string, dynamic> includedItem in folderIncluded)
            // check if the id match...
            if (includedItem.Value.relationships.item.data.id.IndexOf(folderContentItem.Value.id) != -1)
              // found it! now we need to go back on the FOLDER DATA to get the respective FILE for this DOCUMENT
              foreach (KeyValuePair<string, dynamic> folderContentItem1 in folderData)
              {
                if (folderContentItem1.Value.attributes.extension.type.IndexOf("File") == -1)
                  continue; // skip if type is NOT File

                // check if the sourceFileName match...
                if (folderContentItem1.Value.attributes.extension.data.sourceFileName ==
                    includedItem.Value.attributes.extension.data.sourceFileName)
                {
                  // ready!

                  // let's return for the jsTree with a special id:
                  // itemUrn|versionUrn|viewableId
                  // itemUrn: used as target_urn to get document issues
                  // versionUrn: used to launch the Viewer
                  // viewableId: which viewable should be loaded on the Viewer
                  // this information will be extracted when the user click on the tree node, see ForgeTree.js:136 (activate_node.jstree event handler)
                  string treeId = string.Format("{0}|{1}|{2}",
                    folderContentItem.Value.id, // item urn
                    Base64Encode(folderContentItem1.Value.relationships.tip.data.id), // version urn
                    includedItem.Value.attributes.extension.data.viewableId // viewableID
                  );
                  nodes.Add(new JsTreeNode(treeId, WebUtility.UrlDecode(includedItem.Value.attributes.name),
                    "bim360documents", false));
                }
              }
        }
        else
        {
          // non-Plans folder items
          nodes.Add(new JsTreeNode(folderContentItem.Value.links.self.href,
            folderContentItem.Value.attributes.displayName, (string) folderContentItem.Value.type, true));
        }
      }

      return nodes;
    }

    public static async Task<IList<JsTreeNode>> GetItemVersions(string href, Credentials credentials)
    {
      IList<JsTreeNode> nodes = new List<JsTreeNode>();

      // the API SDK
      var itemApi = new ItemsApi();
      itemApi.Configuration.AccessToken = credentials.TokenInternal;

      // extract the projectId & itemId from the href
      var idParams = href.Split('/');
      var itemId = idParams[idParams.Length - 1];
      var projectId = idParams[idParams.Length - 3];

      var versions = await itemApi.GetItemVersionsAsync(projectId, itemId);
      foreach (KeyValuePair<string, dynamic> version in new DynamicDictionaryItems(versions.data))
      {
        DateTime versionDate = version.Value.attributes.lastModifiedTime;
        string verNum = version.Value.id.Split("=")[1];
        string userName = version.Value.attributes.lastModifiedUserName;

        var urn = string.Empty;
        try
        {
          urn = (string) version.Value.relationships.derivatives.data.id;
        }
        catch
        {
          urn = Base64Encode(version.Value.id);
        } // some BIM 360 versions don't have viewable

        var node = new JsTreeNode(
          urn,
          string.Format("v{0}: {1} by {2}", verNum, versionDate.ToString("dd/MM/yy HH:mm:ss"), userName),
          "versions",
          false);
        nodes.Add(node);
      }

      return nodes;
    }

    public static string Base64Encode(string plainText)
    {
      var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
      return Convert.ToBase64String(plainTextBytes).Replace("/", "_");
    }
  }
}