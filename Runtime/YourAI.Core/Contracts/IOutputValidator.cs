using System.Collections.Generic;

namespace YourAI.Core.Contracts
{
    /// <summary>What a validator is handed. Kept deliberately small.</summary>
    public sealed class ValidationInput
    {
        /// <summary>The model's raw output, unmodified.</summary>
        public string RawText;

        /// <summary>JSON Schema the output was asked to satisfy, or null for free text.</summary>
        public string ExpectedSchemaJson;

        /// <summary>Tools that were offered for this turn, so a validator can check tool names.</summary>
        public IReadOnlyList<string> AllowedToolNames;
    }

    /// <summary>Verdict, plus an optional repaired form of the text.</summary>
    public sealed class ValidationResult
    {
        public bool Accepted;

        /// <summary>Why it was rejected. Goes to the log, never to the model.</summary>
        public string Reason;

        /// <summary>
        /// A corrected version of the text, when the repair is mechanical (trailing
        /// comma, code fence wrapper, stray prose). Null means "no repair attempted".
        /// </summary>
        public string NormalizedText;

        public static readonly ValidationResult Ok = new ValidationResult { Accepted = true };

        public static ValidationResult Reject(string reason)
        {
            return new ValidationResult { Accepted = false, Reason = reason };
        }

        public static ValidationResult Repair(string reason, string normalized)
        {
            return new ValidationResult { Accepted = true, Reason = reason, NormalizedText = normalized };
        }
    }

    /// <summary>
    /// Inspects model output before anything acts on it. Validators run in
    /// <see cref="Priority"/> order and the first rejection stops the chain.
    ///
    /// This is the enforcement point for the framework's central contract: the model
    /// says what it wants, and deterministic code decides whether that is allowed.
    /// Validators must be pure functions of their input -- no side effects, no state
    /// mutation -- because a rejected turn may be retried with the same input.
    /// </summary>
    public interface IOutputValidator
    {
        string Name { get; }
        int Priority { get; }

        ValidationResult Validate(ValidationInput input);
    }

    /// <summary>
    /// Ordered validator chain. Supplied here rather than by each host, because
    /// "first rejection wins, repairs replace the text and continue" is framework
    /// behaviour, not application behaviour.
    /// </summary>
    public sealed class ValidatorChain
    {
        private readonly List<IOutputValidator> _validators = new List<IOutputValidator>();

        public IReadOnlyList<IOutputValidator> Validators { get { return _validators; } }

        public void Add(IOutputValidator validator)
        {
            if (validator == null)
            {
                return;
            }
            int at = _validators.Count;
            for (int i = 0; i < _validators.Count; i++)
            {
                if (validator.Priority > _validators[i].Priority)
                {
                    at = i;
                    break;
                }
            }
            _validators.Insert(at, validator);
        }

        public bool Remove(IOutputValidator validator)
        {
            return _validators.Remove(validator);
        }

        /// <summary>
        /// Runs every validator in order. Returns the final verdict; on acceptance the
        /// caller should use <c>NormalizedText</c> when it is non-null, since earlier
        /// repairs must be visible to later validators.
        /// </summary>
        public ValidationResult Run(ValidationInput input)
        {
            if (_validators.Count == 0)
            {
                return ValidationResult.Ok;
            }

            string current = input.RawText;
            for (int i = 0; i < _validators.Count; i++)
            {
                ValidationInput step = new ValidationInput
                {
                    RawText = current,
                    ExpectedSchemaJson = input.ExpectedSchemaJson,
                    AllowedToolNames = input.AllowedToolNames,
                };

                ValidationResult result = _validators[i].Validate(step);
                if (result == null || !result.Accepted)
                {
                    return result ?? ValidationResult.Reject(_validators[i].Name + " returned null");
                }
                if (result.NormalizedText != null)
                {
                    current = result.NormalizedText;
                }
            }

            return new ValidationResult { Accepted = true, NormalizedText = current };
        }
    }
}
