# jira-release-notes-generator

Azure Function to extract/generate release notes from Jira for every CI build 

Usage:
1. Open .sln and deploy function to Azure Function app
2. Set function app's environments variables in azure portal (also create azure storage account to store release notes)
3. Invoke function from Azure Pipeline by sending via POST: fromBuildNumber, currentBuildNumber
4. Release notes will be stored in container, consume/publish .txt file as neccessary 
