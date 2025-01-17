using Serilog;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;

namespace WinFIM.NET_Service
{
    internal class Controller
    {
        private readonly SQLiteHelper _sqLiteHelper;
        private readonly SHA256 _sha256;

        internal Controller()
        {
            _sqLiteHelper = new SQLiteHelper();
            _sha256 = SHA256.Create();
        }

        private static string GetFileOwner(string path)
        {
            string fileOwner;
            try
            {
                if (!(File.Exists(path)))
                {
                    fileOwner = $"File not found: {path}";
                    return fileOwner;
                }

                fileOwner = File.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.NTAccount)).ToString();
            }
            catch
            {
                try
                {
                    fileOwner = File.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).ToString();
                }
                catch (Exception e)
                {
                    var errorMessage = $"Error in GetFileOwner - {e.Message} for path: {path}";
                    Console.WriteLine(errorMessage);
                    fileOwner = "UNKNOWN";
                }
            }

            return fileOwner;
        }

        private static string GetDirectoryOwner(string path)
        {
            string directoryOwner;
            try
            {
                if (!(Directory.Exists(path)))
                {
                    directoryOwner = $"Directory not found: {path}";
                    return directoryOwner;
                }

                directoryOwner = Directory.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.NTAccount)).ToString();
            }
            catch
            {
                try
                {
                    directoryOwner = Directory.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.SecurityIdentifier)).ToString();
                }
                catch (Exception e)
                {
                    var errorMessage = $"Error in GetDirectoryOwner - {e.Message} for path: {path}";
                    Console.WriteLine(errorMessage);
                    directoryOwner = "UNKNOWN";
                }
            }

            return directoryOwner;
        }

        /// <summary>
        /// Get file size in MB
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static string GetFileSize(string path)
        {
            try
            {
                var length = new FileInfo(path).Length;
                return Math.Round(Convert.ToDouble(length) / 1024 / 1024, 3).ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                Log.Error(e, "GetFileSize error");
                return "UNKNOWN";
            }
        }

        /// <summary>
        /// Get file extension exclusion list and construct regex
        /// the return string will be "EMPTY", if there is no file extension exclusion
        /// </summary>
        /// <returns></returns>
        private static string ExcludeExtensionRegex()
        {
            try
            {
                var excludeExtensionPath = LogHelper.WorkDir + "\\exclude_extension.txt";
                var lines = File.ReadAllLines(excludeExtensionPath);
                lines = lines.Distinct().ToArray();
                var extName = new List<string>();

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var temp = line.TrimEnd('\r', '\n');

                    var match = Regex.Match(temp, @"/^[a-zA-Z0-9-_]+$/", RegexOptions.IgnoreCase);
                    //if the file extension does not match the exclusion
                    if (!match.Success)
                    {
                        Log.Verbose("Regex success: " + temp);
                        temp = "[.]" + temp;
                        extName.Add(temp);
                    }
                    else
                    {
                        var errorMessage = "Extension \"" + temp + "\" is invalid, file extension should be alphanumeric and '_' + '-' only.";
                        Log.Error(errorMessage);
                        LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773);
                    }
                }

                var isEmpty = !extName.Any();
                if (isEmpty)
                {
                    return "EMPTY";
                }

                var result = string.Join("|", extName.ToArray());
                var regex = "^.*(" + result + ")$";
                return regex;
            }
            catch (Exception e)
            {
                Log.Error(e, "ExcludeExtensionRegex");
                return "ERROR";
            }
        }

        private byte[] GetHashSha256(string filename)
        {
            using (Stream stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var tempResult = _sha256.ComputeHash(stream);
                stream.Close(); 
                return tempResult;
            }
        }

        private static string BytesToString(byte[] bytes) => 
            bytes.Aggregate("", (current, b) => current + b.ToString("x2"));

        internal void Initialise()
        {
            _sqLiteHelper.EnsureDatabaseExists();
            var exExtHash = "";
            var exPathHash = "";
            var monHash = "";

            try
            {
                //create checksum for config files: exclude_extension.txt | exclude_path.txt | monlist.txt
                exExtHash = BytesToString(GetHashSha256(LogHelper.WorkDir + "\\exclude_extension.txt"));
                exPathHash = BytesToString(GetHashSha256(LogHelper.WorkDir + "\\exclude_path.txt"));
                monHash = BytesToString(GetHashSha256(LogHelper.WorkDir + "\\monlist.txt"));
            }
            catch (Exception e)
            {
                var message = "Exception: " + e.Message + "\nConfig files: exclude_extension.txt | exclude_path.txt | monlist.txt is / are missing or having issue to access.";
                Log.Error(message);
                LogHelper.WriteEventLog(message, EventLogEntryType.Error, 7773);
            }

            //compare the checksum with those stored in the DB
            try
            {
                var resetSql = $@"
                    DELETE FROM CONF_FILE_CHECKSUM;
                    DELETE FROM BASELINE_PATH;
                    DELETE FROM CURRENT_PATH;
                    INSERT INTO CONF_FILE_CHECKSUM (pathname, filehash) VALUES ('{LogHelper.WorkDir}\\exclude_extension.txt','{exExtHash}');
                    INSERT INTO CONF_FILE_CHECKSUM (pathname, filehash) VALUES ('{LogHelper.WorkDir}\\exclude_path.txt','{exPathHash}');
                    INSERT INTO CONF_FILE_CHECKSUM (pathname, filehash) VALUES('{LogHelper.WorkDir}\\monlist.txt', '{monHash}');";
                
                //check if the baseline table is empty (If count is 0 then the table is empty.)
                var output = _sqLiteHelper.ExecuteScalar("SELECT COUNT(*) FROM CONF_FILE_CHECKSUM")?.ToString() ?? "";
                Log.Verbose("Output count conf file hash: " + output);

                if (!output.Equals("3")) //suppose there should be 3 rows, if previous checksum exist
                {
                    try
                    {
                        //no checksum or incompetent checksum, empty all table
                        _sqLiteHelper.ExecuteNonQuery(resetSql);
                    }
                    catch (Exception e)
                    {
                        var errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }
                }
                else
                {
                    //else compare the checksum, if different, store the new checksum into DB, and empty both BASELINE_PATH and CURRENT_PATH
                    var count = 0;

                    var sql = $@"SELECT filehash FROM CONF_FILE_CHECKSUM WHERE pathname='{LogHelper.WorkDir}\\exclude_extension.txt'";
                    output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                    if (output.Equals(exExtHash))
                    {
                        count++;
                    }

                    sql = $"SELECT filehash FROM CONF_FILE_CHECKSUM WHERE pathname='{LogHelper.WorkDir}\\exclude_path.txt'";
                    output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                    if (output.Equals(exExtHash))
                    {
                        count++;
                    }

                    sql = $"SELECT filehash FROM CONF_FILE_CHECKSUM WHERE pathname='{LogHelper.WorkDir}\\monlist.txt'";
                    output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                    if (output.Equals(monHash))
                    {
                        count++;
                    }

                    Log.Verbose("Temp Same Count: " + count);

                    //if all hashes are the same
                    if (count == 3)
                    {
                        //use the same config
                    }
                    else
                    {
                        try
                        {
                            //clear all tables
                            _sqLiteHelper.ExecuteNonQuery(resetSql);
                        }
                        catch (Exception e)
                        {
                            var errorMessage = $"SQLite Exception: {e.Message}";
                            Log.Error(errorMessage);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, e.Message);
                try
                {
                    _sqLiteHelper.ExecuteNonQuery("DELETE FROM CONF_FILE_CHECKSUM");
                }
                catch (Exception e1)
                {
                    var errorMessage = "SQLite Exception: " + e1.Message;
                    Log.Error(errorMessage);
                }
            }
        }

        private bool CheckIfMonListBasePathExists(string path, bool haveBaseLinePath)
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                return true; // we just need to know that the base path exists - the CheckPath method later on will compare hashes
            }

            if (!haveBaseLinePath)
            {
                return false;
            }
            var sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}'";
            var output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
            if (!output.Equals("0"))
            {
                Log.Warning($"Base path from monlist.txt:'{path}' has been deleted.");
            }

            return false;
        }

        private static string[] GetFileMonList()
        {
            //read the monitoring list (line by line)
            var monListPath = LogHelper.WorkDir + "\\monlist.txt";
            string[] monFileLines;
            try
            {
                monFileLines = File.ReadAllLines(monListPath);
            }
            catch (Exception e)
            {
                var errorMessage = $"Exception : {e.Message}{Environment.NewLine}Please make sure all input entries are correct under \"monlist.txt\".\nPlease restart the service after correction.";
                Log.Error(errorMessage);
                LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                throw;
            }

            return monFileLines;
        }

        private string[] GetPathList(string[] monFileLines, bool haveBaseLine)
        {
            var fileList = new List<string>();
            //get the full file mon list for further processing
            foreach (var line in monFileLines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (!(CheckIfMonListBasePathExists(line, haveBaseLine)))
                {
                    continue;
                }

                var fileOrDirectory = new FileInfo(line);

                if (fileOrDirectory.Attributes.HasFlag(FileAttributes.Directory))
                {
                    var files = GetFiles(line);
                    fileList.AddRange(files);
                }
                else
                {
                    fileList.Add(fileOrDirectory.FullName);
                }
            }

            //change all string in filelist to lowercase for easy comparison to exclusion list
            //remove duplicate elements in fileListArray
            var fileListArray = fileList
                .Select(x => x.ToLowerInvariant())
                .Distinct()
                .ToArray();

            return fileListArray;
        }

        private static string[] GetFileExcludePath()
        {
            //read the exclude list (line by line)
            var excludePathFilePath = LogHelper.WorkDir + "\\exclude_path.txt";
            string[] lines;
            try
            {
                lines = File.ReadAllLines(excludePathFilePath);
            }
            catch (Exception e)
            {
                var errorMessage = $"Exception : {e.Message}{Environment.NewLine}Please make sure all input entries are correct under \"exclude_path.txt\".\nPlease restart the service after correction.";
                Log.Error(errorMessage);
                LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                throw;
            }

            return lines;
        }

        private static string[] GetExcludeList(string[] lines)
        {
            var exFileList = new List<string>();
            //get the full exclude file list for further processing
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var fileOrDirectory = new FileInfo(line);
                    if (fileOrDirectory.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        var files = GetFiles(line);
                        exFileList.AddRange(files);
                    }
                    else
                    {
                        exFileList.Add(fileOrDirectory.FullName);
                    }
                }
                catch (Exception e)
                {
                    var errorMessage = "Exclusion error:" + e.Message;
                    Log.Error(errorMessage);
                    //The file path on the exclusion could be not exist
                }
            }
            //change all string in exFileList to lowercase for easy comparison to exclusion list

            var exFileListArray = exFileList
                .Select(x => x.ToLowerInvariant())
                .Distinct()
                .ToArray();

            return exFileListArray;
        }

        private static ICollection<string> GetFiles(string path)
        {
            var files = new List<string>();
            var directories = Array.Empty<string>();
            try
            {
                files.AddRange(Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly));
                directories = Directory.GetDirectories(path);
            }
            // Ignore inaccessible paths
            catch (UnauthorizedAccessException)
            {
            }

            foreach (var directory in directories)
            {
                try
                {
                    files.AddRange(GetFiles(directory));
                }
                // Ignore inaccessible paths
                catch (UnauthorizedAccessException)
                {
                }
            }

            return files;
        }

        private bool CheckBaseLine()
        {
            bool haveBaseline;
            try
            {
                const string sql = "SELECT COUNT(*) FROM BASELINE_PATH";
                var output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                haveBaseline = !output.Equals("0");
                Log.Verbose($"Number of rows in table BASELINE_PATH: {output}");
            }
            catch (Exception e)
            {
                var errorMessage = $"Exception : {e.Message} \nPlease make sure local database file \"fimdb.db\" exists.";
                Log.Error(errorMessage);
                LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                return false;
            }

            return haveBaseline;
        }

        private void CheckPath(bool haveBaseLinePath, string path, int attempt = 0)
        {
            Log.Debug($"Checking path {path}");
            try
            {
                attempt++;
                //1. check the line entry is a file or a directory
                var attr = File.GetAttributes(path);
                if (attr.HasFlag(FileAttributes.Directory))
                {
                    CheckDirectory(haveBaseLinePath, path);
                }
                else
                {
                    CheckFile(haveBaseLinePath, path);
                }
            }
            catch (Exception e)
            {
                if (attempt < 2)
                {
                    Thread.Sleep(500);
                    CheckPath(haveBaseLinePath, path, attempt);
                }
                else
                {
                    var errorMessage = $"File '{path}' could be renamed / deleted during the hash calculation. This file is ignored in this checking cycle - {e.Message}.";
                    Log.Error(errorMessage);
                    LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                }
            }
        }

        private void CheckDirectory(bool haveBaseLinePath, string path)
        {
            var directoryOwner = GetDirectoryOwner(path);
            //if there is content in BASELINE_PATH before, write to CURRENT_PATH
            if (haveBaseLinePath)
            {
                var sql = $"INSERT INTO CURRENT_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,0,'{directoryOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','NA','Directory')";
                _sqLiteHelper.ExecuteNonQuery(sql);

                //compare with BASELINE_PATH
                //1. check if the file exist in BASELINE_PATH
                sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}'";
                var output = _sqLiteHelper.ExecuteScalar(sql).ToString();
                if (!output.Equals("0"))
                {
                    Log.Verbose($"Directory: '{path}' has no change.");
                }
                else
                {
                    var message = $"Directory: '{path}' is newly created. Owner: {directoryOwner}";
                    Log.Warning(message);
                    LogHelper.WriteEventLog(message, EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                }
            }
            //if there is no content in BASELINE_PATH, write to BASELINE_PATH instead
            else
            {
                var sql = $"INSERT INTO BASELINE_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,0,'{directoryOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','NA','Directory')";
                _sqLiteHelper.ExecuteScalar(sql);
                Log.Debug($"Directory {path} exists");
            }
        }

        private void CheckFile(bool haveBaseLinePath, string path)
        {
            var regex = ExcludeExtensionRegex(); //get the regex of file extension exclusion
            var fileOwner = GetFileOwner(path);
            Log.Verbose("File Extension Exclusion REGEX:" + regex);
            string message;

            //a. if there is file extension exclusion
            string tempHash;
            if (regex.Equals("EMPTY"))
            {
                try
                {
                    tempHash = BytesToString(GetHashSha256(path));
                }
                catch (Exception e)
                {
                    tempHash = "UNKNOWN";
                    var errorMessage = $"File '{path}' is locked and not accessible for Hash calculation - {e.Message}.";
                    Log.Error(errorMessage);
                    LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773);
                }

                //if there is content in BASELINE_PATH before, write to CURRENT_PATH
                if (haveBaseLinePath)
                {
                    var sql = $"INSERT INTO CURRENT_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,'{GetFileSize(path)}','{fileOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','{tempHash}','File'";
                    _sqLiteHelper.ExecuteNonQuery(sql);

                    //compare with BASELINE_PATH
                    //1. check if the file exist in BASELINE_PATH
                    sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}'";
                    var output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                    if (!output.Equals("0"))
                    {
                        //1. check if the file hash in BASELINE_PATH changed
                        sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}' AND filehash='{tempHash}'";
                        output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                        if (!output.Equals("0"))
                        {
                            Log.Verbose($"File: '{path}' has no change.");
                        }
                        else
                        {
                            sql = "SELECT pathname, pathexists, filesize, owner, filehash, checktime FROM BASELINE_PATH WHERE pathname=@path";
                            _sqLiteHelper.ExecuteReader(dataReader =>
                                {
                                    if (!dataReader.Read())
                                    {
                                        return;
                                    }
                                    message = $"File: '{path}' is modified. Previous-check: {dataReader.GetValue(5)} " +
                                              $"Hash: (Previous){dataReader.GetValue(4)} (Current){tempHash} " +
                                              $"Size: (Previous){dataReader.GetValue(2)}MB (Current){GetFileSize(path)}MB " +
                                              $"File Owner: (Previous){dataReader.GetValue(3)} (Current){fileOwner}";
                                    Log.Warning(message);
                                    LogHelper.WriteEventLog(message, EventLogEntryType.Warning, 7777);
                                },
                                sql,
                                new SQLiteParameter("@path", path));
                        }
                    }
                    else
                    {
                        message = $"File: '{path}' is newly created. \nOwner: {fileOwner} \nHash: {tempHash}";
                        Log.Warning(message);
                        LogHelper.WriteEventLog($"File: '{path}' is newly created.\nOwner: {fileOwner} Hash: {tempHash}", EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                    }
                }
                //if there is no content in BASELINE_PATH, write to BASELINE_PATH instead
                else
                {
                    var sql = $"INSERT INTO BASELINE_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,{GetFileSize(path)},'{fileOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','{tempHash}','File')";
                    Log.Verbose(sql);
                    try
                    {
                        _sqLiteHelper.ExecuteNonQuery(sql);
                    }
                    catch (Exception e)
                    {
                        var errorMessage = "SQLite Exception: " + e.Message;
                        Log.Error(errorMessage);
                    }
                }
            }
            //b. if there is file extension exclusion
            else
            {
                var match = Regex.Match(path, regex, RegexOptions.IgnoreCase);
                //if the file extension does not match the exclusion
                if (!match.Success)
                {
                    try
                    {
                        tempHash = BytesToString(GetHashSha256(path));
                    }
                    catch (Exception e)
                    {
                        tempHash = "UNKNOWN";
                        message = $"File '{path}' is locked and not accessible for Hash calculation - {e.Message}.";
                        Log.Error(message);
                        LogHelper.WriteEventLog(message, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
                    }

                    if (haveBaseLinePath)
                    {
                        var sql = $"INSERT INTO CURRENT_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,{GetFileSize(path)},'{fileOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','{tempHash}','File')";
                        try
                        {
                            _sqLiteHelper.ExecuteNonQuery(sql);
                        }
                        catch (Exception e)
                        {
                            message = "SQLite Exception: " + e.Message;
                            Log.Error(message);
                        }

                        //compare with BASELINE_PATH
                        //1. check if the file exist in BASELINE_PATH
                        sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}'";
                        var output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                        if (!output.Equals("0"))
                        {
                            //1. check if the file hash in BASELINE_PATH changed
                            sql = $"SELECT COUNT(*) FROM BASELINE_PATH WHERE pathname='{path}' AND filehash='{tempHash}'";
                            output = _sqLiteHelper.ExecuteScalar(sql)?.ToString() ?? "";
                            if (!output.Equals("0"))
                            {
                                Log.Verbose($"File: '{path}' has no change.");
                            }
                            else
                            {
                                sql = "SELECT pathname, pathexists, filesize, owner, filehash, checktime FROM BASELINE_PATH WHERE pathname=@path";
                                _sqLiteHelper.ExecuteReader(dataReader =>
                                    {
                                        if (!dataReader.Read())
                                        {
                                            return;
                                        }
                                        message = $"File: '{path}' is modified. Previous check at:{dataReader.GetValue(5)} " +
                                                  $"Hash: (Previous){dataReader.GetValue(4)} (Current){tempHash} " +
                                                  $"Size: (Previous){dataReader.GetValue(2)}MB (Current){GetFileSize(path)}MB " +
                                                  $"File Owner: (Previous){dataReader.GetValue(3)} (Current){fileOwner}";
                                        Log.Warning(message);
                                        LogHelper.WriteEventLog(message, EventLogEntryType.Warning, 7777); //setting the Event ID as 7777
                                    }, 
                                    sql, 
                                    new SQLiteParameter("@path", path));
                            }
                        }
                        else
                        {
                            message = $"File: '{path}' is newly created. Owner: {fileOwner} Hash: {tempHash}";
                            Log.Warning(message);
                            LogHelper.WriteEventLog(message, EventLogEntryType.Warning, 7776); //setting the Event ID as 7776
                        }
                    }
                    //if there is no content in BASELINE_PATH, write to BASELINE_PATH instead
                    else
                    {
                        var sql = $"INSERT INTO BASELINE_PATH (pathname, pathexists, filesize, owner, checktime, filehash, pathtype) VALUES ('{path}',true,{GetFileSize(path)},'{fileOwner}','(UTC){DateTime.UtcNow:yyyy/MM/dd hh:mm:ss tt}','{tempHash}','File')";
                        try
                        {
                            _sqLiteHelper.ExecuteNonQuery(sql);
                        }
                        catch (Exception e)
                        {
                            var errorMessage = "SQLite Exception: " + e.Message;
                            Log.Error(errorMessage);
                        }
                    }
                }
            }
        }

        private void CheckIfDeleted(bool haveBaseLinePath)
        {
            if (!haveBaseLinePath)
            {
                return;
            }

            const string sql = "SELECT BASELINE_PATH.pathname, BASELINE_PATH.pathtype FROM BASELINE_PATH LEFT JOIN CURRENT_PATH ON BASELINE_PATH.pathname = CURRENT_PATH.pathname WHERE CURRENT_PATH.pathname IS NULL";
            _sqLiteHelper.ExecuteReader(dataReader =>
                {
                    while (dataReader.Read())
                    {
                        var deletedPathName = dataReader.GetValue(0).ToString();
                        var deletedPathType = dataReader.GetValue(1).ToString();
                        var deletedMessage = $"{deletedPathType}: '{deletedPathName}' has been deleted.";
                        Log.Warning(deletedMessage);
                        LogHelper.WriteEventLog(deletedMessage, EventLogEntryType.Warning, 7778); //setting the Event ID as 7778
                    }
                },
                sql);
        }

        private void ResetDatabaseTables(bool haveBaseLinePath)
        {
            if (!haveBaseLinePath)
            {
                return;
            }

            //delete all rows in BASELINE_PATH, copy all rows from CURRENT_PATH to BASELINE_PATH, then clear CURRENT_PATH
            try
            {
                _sqLiteHelper.ExecuteNonQuery("DELETE FROM BASELINE_PATH");
            }
            catch (Exception e)
            {
                var errorMessage = "SQLite Exception: " + e.Message;
                Log.Error(errorMessage);
            }

            try
            {
                _sqLiteHelper.ExecuteNonQuery("INSERT INTO BASELINE_PATH SELECT * FROM CURRENT_PATH");
            }
            catch (Exception e)
            {
                var errorMessage = "SQLite Exception: " + e.Message;
                Log.Error(errorMessage);
            }

            try
            {
                _sqLiteHelper.ExecuteNonQuery("DELETE FROM CURRENT_PATH");
            }
            catch (Exception e)
            {
                var errorMessage = "SQLite Exception: " + e.Message;
                Log.Error(errorMessage);
            }
        }

        internal void FileIntegrityCheck()
        {
            var schedulerMin = LogHelper.GetSchedule();
            Log.Information($"Starting FIM checks on a {schedulerMin} minute timer");
            if (Properties.Settings.Default.is_capture_remote_connection_status)
            {
                Log.Information(LogHelper.GetRemoteConnections());
            }

            var haveBaseLinePath = CheckBaseLine(); //check if there is already data in the BASELINE_PATH table from a previous FIM check

            var watch = new Stopwatch();
            watch.Start();

            try
            {
                var monListFileLines = GetFileMonList(); //get the list of paths in the monlist.txt file
                var pathList = GetPathList(monListFileLines, haveBaseLinePath); //get the list of files / directories to watch
                var excludePathLines = GetFileExcludePath(); //get the list of paths in the exclude_path.txt file
                var excludePathList = GetExcludeList(excludePathLines);
                var finalPathList = pathList.Except(excludePathList); //filter exclusion file list
                
                foreach (var path in finalPathList)
                {
                    CheckPath(haveBaseLinePath, path);
                }

                CheckIfDeleted(haveBaseLinePath);
                ResetDatabaseTables(haveBaseLinePath);

                watch.Stop();
                var stopMessage =
                    $"Total time consumed in this round of file integrity checking  = {watch.ElapsedMilliseconds}ms ({Math.Round(Convert.ToDouble(watch.ElapsedMilliseconds) / 1000, 3)}s).{Environment.NewLine}{LogHelper.GetRemoteConnections()}";
                Log.Debug(stopMessage);
                LogHelper.WriteEventLog(stopMessage, EventLogEntryType.Information, 7771); //setting the Event ID as 7771
                Log.Verbose(stopMessage);
            }
            catch (Exception e)
            {
                var errorMessage = $"Exception : {e.Message}{Environment.NewLine}Please make sure all input entries are correct under \"monlist.txt\", \"exclude_path.txt\" and \"exclude_extension.txt\".\nPlease restart the service after correction.";
                Log.Error(errorMessage);
                LogHelper.WriteEventLog(errorMessage, EventLogEntryType.Error, 7773); //setting the Event ID as 7773
            }
        }
    }
}