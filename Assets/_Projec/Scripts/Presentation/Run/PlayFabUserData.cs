using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;

namespace Game.Presentation.Run
{
    /// <summary>
    /// Acceso del cliente al perfil en PlayFab. El loadout y el stash son Player Data de SOLO
    /// LECTURA para el cliente: se leen directo, pero toda escritura pasa por las funciones de
    /// CloudScript (PlayFab/cloudscript.js), que validan que no aparezcan items de la nada.
    /// Requiere sesión activa; ver PlayFabSession.
    /// </summary>
    public static class PlayFabUserData
    {
        public const string EnsureProfileFunction = "EnsureProfile";
        public const string CommitProfileFunction = "CommitProfile";
        public const string AbandonActiveRunFunction = "AbandonActiveRun";

        [Serializable]
        private class CloudResult
        {
            public bool ok;
            public string reason;
        }

        /// <summary>La función de CloudScript respondió que no (regla de negocio): no reintentar.</summary>
        public class RejectedException : Exception
        {
            public string Reason { get; }
            public RejectedException(string function, string reason) : base($"{function} rechazado: {reason}") => Reason = reason;
        }

        /// <summary>Lee claves de la Player Data de solo lectura. Las que no existen no vienen.</summary>
        public static Task<Dictionary<string, string>> ReadAsync(params string[] keys)
        {
            var tcs = new TaskCompletionSource<Dictionary<string, string>>();

            PlayFabClientAPI.GetUserReadOnlyData(
                new GetUserDataRequest { Keys = new List<string>(keys) },
                result =>
                {
                    var values = new Dictionary<string, string>();
                    if (result.Data != null)
                        foreach (var kvp in result.Data)
                            if (!string.IsNullOrEmpty(kvp.Value?.Value)) values[kvp.Key] = kvp.Value.Value;
                    tcs.SetResult(values);
                },
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            return tcs.Task;
        }

        /// <summary>
        /// Ejecuta una función de CloudScript. Error de red o del script: Exception (se puede
        /// reintentar). La función respondió que no: RejectedException (no tiene sentido reintentar).
        /// </summary>
        public static Task CallAsync(string function, Dictionary<string, string> args = null)
        {
            var tcs = new TaskCompletionSource<bool>();

            PlayFabClientAPI.ExecuteCloudScript(
                new ExecuteCloudScriptRequest
                {
                    FunctionName = function,
                    FunctionParameter = args ?? new Dictionary<string, string>(),
                    GeneratePlayStreamEvent = false,
                },
                result =>
                {
                    if (result.Error != null)
                    {
                        tcs.SetException(new Exception($"{function}: error del script: {result.Error.Error} {result.Error.Message}"));
                        return;
                    }

                    CloudResult parsed = null;
                    try { parsed = JsonUtility.FromJson<CloudResult>(result.FunctionResult as string); }
                    catch (Exception) { /* respuesta inesperada: se trata abajo */ }

                    if (parsed == null)
                        tcs.SetException(new Exception($"{function}: respuesta inesperada ({result.FunctionResult})"));
                    else if (!parsed.ok)
                        tcs.SetException(new RejectedException(function, parsed.reason));
                    else
                        tcs.SetResult(true);
                },
                error => tcs.SetException(new Exception(error.GenerateErrorReport())));

            return tcs.Task;
        }
    }
}
